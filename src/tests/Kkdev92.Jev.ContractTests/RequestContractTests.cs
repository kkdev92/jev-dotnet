using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kkdev92.Jev.TestSupport;
using Kkdev92.Jev.Wire;
using static Kkdev92.Jev.ContractTests.Clients;

namespace Kkdev92.Jev.ContractTests;

/// <summary>The exact request the client sends, and the result it builds from a good answer.</summary>
public sealed class RequestContractTests
{
    [Fact]
    public async Task AnEvaluationIsOnePostToTheSystemOneEndpoint()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var (plan, _, _) = Plan();

        await Create(handler).EvaluateAsync("I was charged twice.", plan, cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(new Uri("https://api.typesafe.ai/v1/systemone"), request.Uri);
    }

    [Fact]
    public async Task TheHeadersAreTheBearerKeyJsonAndTheSdksOwnUserAgentOnly()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var (plan, _, _) = Plan();

        await Create(handler).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("Bearer " + ApiKey, request.Headers["Authorization"]);
        Assert.Equal("application/json", request.Headers["Accept"]);
        Assert.StartsWith("Kkdev92.Jev/", request.Headers["User-Agent"], StringComparison.Ordinal);
        Assert.Equal("application/json", request.ContentHeaders["Content-Type"]);

        // The SDK identifies itself by its User-Agent alone, and sends no X-TypeSafe-* header.
        Assert.DoesNotContain(request.Headers.Keys, k => k.StartsWith("X-TypeSafe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HttpTwoIsRequestedAndOneOneAccepted()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var (plan, _, _) = Plan();

        var result = await Create(handler).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
        Assert.Equal(HttpVersion.Version20, result.Metadata.HttpVersion);
    }

    /// <summary>What goes over the wire is read back through the models generated from the service's own OpenAPI document.</summary>
    [Fact]
    public async Task TheBodyConformsToTheOpenApiContract()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var (plan, _, _) = Plan();

        await Create(handler).EvaluateAsync("決済が失敗しました 😀", plan, new JevRequestOptions { Model = "jev-1.13.0" }, TestContext.Current.CancellationToken);

        var body = Assert.Single(handler.Requests).Body;
        var request = JsonSerializer.Deserialize(body, JevWireJsonContext.Default.SystemOneRequest)!;
        var validation = new WireValidation();
        request.Validate(validation, "$");

        Assert.True(validation.IsValid, string.Join("; ", validation.Violations));
        Assert.Equal("jev-1.13.0", request.Model);
        Assert.Equal("決済が失敗しました 😀", request.State.GetString());
        Assert.Equal(["tone", "urgent"], request.Questions.Keys);
    }

    [Fact]
    public async Task ATypedStateIsSerializedWithTheCallersMetadata()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var (plan, _, _) = Plan();

        await Create(handler).EvaluateAsync(new Ticket("Duplicate charge", ["I was charged twice."]), ContractJsonContext.Default.Ticket, plan, cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        var state = document.RootElement.GetProperty("state");

        Assert.Equal("Duplicate charge", state.GetProperty("subject").GetString());
        Assert.Equal("I was charged twice.", state.GetProperty("messages")[0].GetString());
    }

    [Fact]
    public async Task ATypedStateThatIsNotAnObjectArrayOrStringFailsTheTaskAndSendsNothing()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var (plan, _, _) = Plan();

        var call = Create(handler).EvaluateAsync(42, ContractJsonContext.Default.Int32, plan, cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentException>(() => call);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PreparedContentIsSentAsJsonNotAsAString()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var (plan, _, _) = Plan();
        var state = JevContent.FromJson("""{"ticket":{"subject":"Refund"}}""");

        await Create(handler).EvaluateContentAsync(state, plan, cancellationToken: TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("Refund", document.RootElement.GetProperty("state").GetProperty("ticket").GetProperty("subject").GetString());
    }

    [Fact]
    public async Task ArgumentsAreCheckedBeforeTheCallStarts()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        var client = Create(handler);
        var (plan, _, _) = Plan();
        var token = TestContext.Current.CancellationToken;

        // Thrown synchronously, before a task exists: the discard makes each lambda an Action.
        Assert.Throws<ArgumentNullException>(() => { _ = client.EvaluateAsync((string)null!, plan, cancellationToken: token); });
        Assert.Throws<ArgumentNullException>(() => { _ = client.EvaluateAsync("text", null!, cancellationToken: token); });
        Assert.Throws<ArgumentNullException>(() => { _ = client.EvaluateAsync<Ticket>(null!, ContractJsonContext.Default.Ticket, plan, cancellationToken: token); });
        Assert.Throws<ArgumentNullException>(() => { _ = client.EvaluateAsync(new Ticket("s", []), null!, plan, cancellationToken: token); });
        Assert.Throws<ArgumentException>(() => { _ = client.EvaluateContentAsync(JevContent.Null, plan, cancellationToken: token); });
        Assert.Throws<ArgumentException>(() => { _ = client.EvaluateContentAsync(default, plan, cancellationToken: token); });
        Assert.Throws<ArgumentException>(() => { _ = client.EvaluateAsync("bad" + (char)0xDC00, plan, cancellationToken: token); });
        Assert.ThrowsAny<ArgumentException>(() => { _ = client.EvaluateAsync("text", plan, new JevRequestOptions { Model = "" }, token); });

        await Task.Yield();
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AResultCarriesEveryAnswerTheModelAndTheUsage()
    {
        var handler = FakeHttpMessageHandler.Always(Answer(urgent: 0.97));
        var (plan, tone, urgent) = Plan();

        var result = await Create(handler).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Tone.Frustrated, result.Get(tone).Value);
        Assert.Equal(0.84, result.Get(tone).GetProbability("frustrated"));
        Assert.Equal(0.97, result.Get(urgent).Probability);
        Assert.Equal("jev-latest", result.RequestedModel);
        Assert.Equal("jev-1.13.0", result.ActualModel);
        Assert.Equal(new JevUsage(120, 12), result.Usage);
        Assert.Equal(FakeResponses.RequestId, result.Metadata.RequestId);
        Assert.Equal(1, result.Metadata.Attempts);
        Assert.Empty(result.Metadata.Warnings);
    }

    [Fact]
    public async Task TheRawBodyIsKeptOnlyWhenAskedAndOutlivesTheResponse()
    {
        var answer = Answer();
        var handler = FakeHttpMessageHandler.Always(answer);
        var (plan, _, _) = Plan();
        var client = Create(handler);

        var plain = await client.EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);
        var captured = await client.EvaluateAsync("text", plan, new JevRequestOptions { CaptureRawResponse = true }, TestContext.Current.CancellationToken);

        Assert.Null(plain.RawResponseBody);
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(answer), captured.RawResponseBody!.Value.ToArray());
    }

    [Fact]
    public async Task ACustomOriginIsUsedForEveryPath()
    {
        var handler = new FakeHttpMessageHandler((request, _) => Task.FromResult(FakeResponses.Json(request.Uri.AbsolutePath.EndsWith("models", StringComparison.Ordinal) ? FakeResponses.Models() : Answer())));
        var (plan, _, _) = Plan();
        var client = Create(handler, o => o.BaseAddress = new Uri("https://gateway.example"));

        await client.EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);
        await client.GetModelsAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["https://gateway.example/v1/systemone", "https://gateway.example/v1/models"], handler.Requests.Select(r => r.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task TheHttpClientIsNeverReconfigured()
    {
        var handler = FakeHttpMessageHandler.Always(Answer());
        using var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(100) };
        var client = new JevClient(httpClient, new JevClientOptions { Credential = new StaticJevCredential(ApiKey) });
        var (plan, _, _) = Plan();

        await client.EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(httpClient.DefaultRequestHeaders);
        Assert.Null(httpClient.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(100), httpClient.Timeout);
    }

    [Fact]
    public async Task ModelsAreListedWithAGetAndNoBody()
    {
        var handler = FakeHttpMessageHandler.Always(FakeResponses.Models());

        var models = await Create(handler).GetModelsAsync(cancellationToken: TestContext.Current.CancellationToken);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(new Uri("https://api.typesafe.ai/v1/models"), request.Uri);
        Assert.Empty(request.Body);
        Assert.Equal(["jev-latest", "jev-preview"], models.Select(m => m.Name));
        Assert.Equal("2026-09-15", models[0].ReleaseDate);
        Assert.Equal("jev-latest", models[0].ToString());
    }

    [Fact]
    public async Task AModelListWithoutRequiredFieldsIsRefused()
    {
        var handler = FakeHttpMessageHandler.Always("""{"models":[{"name":"jev-latest"}]}""");

        var exception = await Assert.ThrowsAsync<JevProtocolException>(() => Create(handler).GetModelsAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JevOperation.ListModels, exception.Operation);
    }

    [Fact]
    public async Task OnePlanServesManyConcurrentCallsWithoutMixingThemUp()
    {
        // The answer depends on the state, so a result delivered to the wrong call would show.
        var handler = new FakeHttpMessageHandler(async (request, cancellationToken) =>
        {
            using var document = JsonDocument.Parse(request.Body);
            var n = int.Parse(document.RootElement.GetProperty("state").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            await Task.Delay(n % 7, cancellationToken);
            return FakeResponses.Json(Answer(urgent: n / 100.0));
        });

        var (plan, _, urgent) = Plan();
        var client = Create(handler);

        var calls = Enumerable.Range(0, 100)
            .Select(async n => (n, result: await client.EvaluateAsync(n.ToString(System.Globalization.CultureInfo.InvariantCulture), plan, cancellationToken: TestContext.Current.CancellationToken)));

        foreach (var (n, result) in await Task.WhenAll(calls))
        {
            Assert.Equal(n / 100.0, result.Get(urgent).Probability);
        }
    }

    internal sealed record Ticket(string Subject, string[] Messages);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RequestContractTests.Ticket))]
[JsonSerializable(typeof(int))]
internal sealed partial class ContractJsonContext : JsonSerializerContext;
