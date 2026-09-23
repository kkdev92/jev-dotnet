using System.Text.Json;
using System.Text.Json.Serialization;
using Kkdev92.Jev;
using Kkdev92.Jev.AotSmokeTests;
using Kkdev92.Jev.DependencyInjection;
using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.DependencyInjection;

// Native AOT smoke application.
// A library that merely compiles is not evidence of AOT compatibility. This is a real consumer that
// CI publishes with PublishAot=true, so the trimmer and ILCompiler see the code paths an
// application would use: text, typed and structured state; choices over an enum, strings and a
// struct of the caller's own; a score with a structured legend; the model list; every kind of
// failure a caller has to handle; and the container composing a client. Every response comes from
// an in-process fake handler, so this never reaches the network.

var failures = 0;

void Check(string name, bool condition)
{
    Console.WriteLine($"{(condition ? "ok  " : "FAIL")}  {name}");

    if (!condition)
    {
        failures++;
    }
}

async Task Expect<TException>(string name, Func<Task> call, Func<TException, bool> check)
    where TException : Exception
{
    try
    {
        await call();
        Check(name + " (nothing was thrown)", false);
    }
    catch (TException ex)
    {
        Check(name, check(ex));
    }
}

static JevClient Client(HttpMessageHandler handler) => new(
    new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
    new JevClientOptions { Credential = new StaticJevCredential("sk-aot-smoke-not-a-key") });

Console.WriteLine($"base address: {JevClientOptions.DefaultBaseAddress}");
Console.WriteLine($"default model: {JevClientOptions.DefaultModelName}");
Console.WriteLine();

// A plan with every question kind, and a choice over each kind of value a caller might map to.
var builder = new JevDecisionPlanBuilder();

var department = builder.AddChoice<Department>("department", "Which team should handle this?",
[
    new(Department.Billing, "billing", "Payments, invoicing, refunds"),
    new(Department.Technical, "technical", "Bugs, outages, integrations"),
]);

var language = builder.AddChoice<string>("language", "Which language is the message in?",
[
    new("en", "english"),
    new("ja", "japanese"),
]);

var route = builder.AddChoice<Route>("route", "Where should it be queued?",
[
    new(new Route("standard", 3), "standard"),
    new(new Route("priority", 1), "priority"),
]);

var mood = builder.AddScore("mood", "How does the customer feel?",
[
    JevContent.FromJson("""{"label":"calm","hint":"no emotional language"}"""),
    JevContent.FromJson("""{"label":"upset","hint":"complaints, but civil"}"""),
    JevContent.FromJson("""{"label":"angry","hint":"threats to leave"}"""),
]);

var urgent = builder.AddNoul("urgent", "Does this need an answer today?", new JevNoulCriteria("A deadline or an outage", "Anything else"));

var plan = builder.Build();

Check("plan builds", plan.QuestionCount == 5);

var response = new SystemOneResponseBuilder()
    .Choice("department", "billing", 0.9, ("billing", 0.95), ("technical", 0.05))
    .Choice("language", "japanese", 0.99, ("english", 0.005), ("japanese", 0.995))
    .Choice("route", "priority", 0.6, ("standard", 0.2), ("priority", 0.8))
    .Raw("mood", """{"type":"score","score":1.1,"confidence":0.7,"legend":{"0":{"label":"calm","hint":"no emotional language"},"1":{"label":"upset","hint":"complaints, but civil"},"2":{"label":"angry","hint":"threats to leave"}},"probabilities":{"0":0.1,"1":0.7,"2":0.2}}""")
    .Noul("urgent", 0.85)
    .Build();

// Text state.
var handler = FakeHttpMessageHandler.Always(response);
var client = Client(handler);
var result = await client.EvaluateAsync("請求が二重に発生しています。今日中に対応してください。", plan);

Check("enum choice maps back", result.Get(department).Value == Department.Billing);
Check("string choice maps back", result.Get(language).Value == "ja");
Check("struct choice maps back", result.Get(route).Value == new Route("priority", 1));
Check("choice probabilities are read", Math.Abs(result.Get(route).GetProbability("priority") - 0.8) < 1e-12);
Check("score expectation is read", Math.Abs(result.Get(mood).Value - 1.1) < 1e-12);
Check("structured legend survives", result.Get(mood).Legend[2].GetProperty("label").GetString() == "angry");
Check("noul probability is read", Math.Abs(result.Get(urgent).Probability - 0.85) < 1e-12);
Check("usage is read", result.Usage.InputTokens == 120 && result.Usage.OutputTokens == 12);
Check("request id is kept", result.Metadata.RequestId == FakeResponses.RequestId);
Check("state is sent as a JSON string", handler.Requests[0].BodyText.Contains("\"state\":\"", StringComparison.Ordinal));

// Typed state, through source-generated metadata only.
handler = FakeHttpMessageHandler.Always(response);
client = Client(handler);
_ = await client.EvaluateAsync(new Ticket("Charged twice", "The invoice was charged twice.", 2), SmokeJson.Default.Ticket, plan);

Check("typed state is sent as an object", handler.Requests[0].BodyText.Contains("\"state\":{\"subject\":\"Charged twice\"", StringComparison.Ordinal));

// Structured state, from JSON text and from an element.
handler = FakeHttpMessageHandler.Always(response);
client = Client(handler);
_ = await client.EvaluateContentAsync(JevContent.FromJson("""[{"from":"customer","text":"Charged twice"}]"""), plan);

using (var document = JsonDocument.Parse("""{"channel":"email","text":"Charged twice"}"""))
{
    _ = await client.EvaluateContentAsync(JevContent.FromJsonElement(document.RootElement), plan);
}

Check("array state is sent as an array", handler.Requests[0].BodyText.Contains("\"state\":[{", StringComparison.Ordinal));
Check("element state is sent as an object", handler.Requests[1].BodyText.Contains("\"state\":{\"channel\":\"email\"", StringComparison.Ordinal));

// The model list.
var models = await Client(FakeHttpMessageHandler.Always(FakeResponses.Models())).GetModelsAsync();
Check("model list is read", models.Count > 0 && models.Any(m => m.Name.Length > 0));

// Failures, each through its own exception type.
await Expect<JevHttpException>(
    "a missing key is an HTTP error",
    () => Client(FakeHttpMessageHandler.Sequence(FakeResponses.MissingKey)).EvaluateAsync("text", plan),
    ex => ex.StatusCode == 403 && ex.RequestId == FakeResponses.RequestId);

await Expect<JevHttpException>(
    "a validation failure lists its errors without the input",
    () => Client(FakeHttpMessageHandler.Sequence(() => FakeResponses.ValidationFailed())).EvaluateAsync("text", plan),
    ex => ex.StatusCode == 422 && ex.ValidationErrors.Count > 0 && !ex.Message.Contains("SECRET", StringComparison.Ordinal));

await Expect<JevHttpException>(
    "an overloaded service is recognised",
    () => Client(FakeHttpMessageHandler.Sequence(FakeResponses.Overloaded)).EvaluateAsync("text", plan),
    ex => ex.IsOverloaded);

await Expect<JevProtocolException>(
    "a response that does not answer the plan is refused",
    () => Client(FakeHttpMessageHandler.Always(new SystemOneResponseBuilder().Noul("urgent", 0.5).Build())).EvaluateAsync("text", plan),
    ex => ex.Error == JevProtocolError.MissingAnswer);

await Expect<JevTransportException>(
    "a refused connection is a transport error that sent nothing",
    () => Client(new FakeHttpMessageHandler((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"))).EvaluateAsync("text", plan),
    ex => ex.Failure == JevTransportFailure.Connect && ex.Transmission == JevRequestTransmission.NotStarted);

// The container composes a client whose transport is the one the DI package promises.
var services = new ServiceCollection();
services.AddJev(o => o.Credential = new StaticJevCredential("sk-aot-smoke-not-a-key"))
    .ConfigurePrimaryHttpMessageHandler(() => FakeHttpMessageHandler.Always(response));

using (var provider = services.BuildServiceProvider())
{
    var fromContainer = provider.GetRequiredService<JevClient>();
    var answered = await fromContainer.EvaluateAsync("text", plan);

    Check("container composes a working client", answered.Get(department).Value == Department.Billing);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "aot smoke ok" : $"{failures} check(s) failed");

return failures == 0 ? 0 : 1;

namespace Kkdev92.Jev.AotSmokeTests
{
    internal enum Department
    {
        Billing,
        Technical,
    }

    internal readonly record struct Route(string Queue, int Priority);

    internal sealed record Ticket(string Subject, string Body, int Priority);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(Ticket))]
    internal sealed partial class SmokeJson : JsonSerializerContext;
}
