using Kkdev92.Jev;
using Triage;

// Routes one support message: which team, how frustrated, and whether it needs an answer today,
// all from a single call.
//
//   TYPESAFE_API_KEY=... dotnet run --project src/samples/Triage.Console -- "message text"
//
// Every run is one billed evaluation. The key is read from the environment because this sample
// asks for it explicitly; the client itself never looks for one.

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(StaticJevCredential.DefaultEnvironmentVariable)))
{
    Console.Error.WriteLine($"Set {StaticJevCredential.DefaultEnvironmentVariable} to a TypeSafe API key. Each run is one billed evaluation.");
    return 2;
}

var message = args.Length > 0
    ? string.Join(' ', args)
    : "I was charged twice for my subscription this month and I need the second payment refunded before Friday.";

// Built once and reused for every message: the questions are validated and encoded here, not on
// each call. The handles it returns are how the answers are read back, typed.
var builder = new JevDecisionPlanBuilder();

var department = builder.AddChoice<Department>(
    "department",
    "Which team should handle this message?",
    [
        new(Department.Billing, "billing", "Payments, invoices, refunds"),
        new(Department.Technical, "technical", "Bugs, outages, integrations"),
        new(Department.Sales, "sales", "Pricing, upgrades, new accounts"),
    ]);

var frustration = builder.AddScore(
    "frustration",
    "How frustrated is the customer?",
    ["Calm", "Frustrated", "Very angry"]);

var urgent = builder.AddNoul("urgent", "Does the customer need an answer today?");

var plan = builder.Build();

// One long-lived HttpClient for the process. Redirects are not followed, so the key is never
// replayed to another host, and the HttpClient has no timeout of its own: the client's deadline
// (30 seconds by default, retries included) is the one that applies.
using var httpClient = new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
})
{
    Timeout = Timeout.InfiniteTimeSpan,
};

var client = new JevClient(httpClient, new JevClientOptions
{
    Credential = StaticJevCredential.FromEnvironmentVariable(),
});

try
{
    var result = await client.EvaluateAsync(message, plan);

    var team = result.Get(department);
    var mood = result.Get(frustration);
    var today = result.Get(urgent);

    Console.WriteLine($"department   {team.Value} ({team.Confidence:P0} confidence)");
    Console.WriteLine($"frustration  {mood.Value:0.00} on a 0-{mood.LevelCount - 1} scale ({mood.Confidence:P0} confidence)");
    Console.WriteLine($"urgent       {today.Probability:P0}");
    Console.WriteLine();
    Console.WriteLine($"model {result.ActualModel}, {result.Usage.InputTokens} tokens in, {result.Usage.OutputTokens} out, request {result.Metadata.RequestId}");

    return 0;
}
catch (JevHttpException ex) when (ex.StatusCode is 401 or 403)
{
    Console.Error.WriteLine("The API key was not accepted.");
    return 1;
}
catch (JevHttpException ex) when (ex.IsRateLimited || ex.IsOverloaded)
{
    Console.Error.WriteLine($"The service asked us to slow down{(ex.RetryAfter is { } wait ? $"; it suggested waiting {wait.TotalSeconds:0.#} s" : string.Empty)}.");
    return 1;
}
catch (JevException ex)
{
    // The message names what failed without repeating the state, the key or the server's text.
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (JevTimeoutException ex)
{
    Console.Error.WriteLine($"No answer within {ex.Timeout.TotalSeconds:0} s.");
    return 1;
}

namespace Triage
{
    internal enum Department
    {
        Billing,
        Technical,
        Sales,
    }
}
