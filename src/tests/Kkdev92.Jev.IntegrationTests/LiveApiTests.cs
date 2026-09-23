using Kkdev92.Jev.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Kkdev92.Jev.IntegrationTests;

/// <summary>
/// Tests that talk to the real TypeSafe API with a key, and are billed.
/// </summary>
/// <remarks>
/// <para>
/// These never gate a pull request, but they are not excluded from CI either: under the test
/// platform this repository uses, an assembly that matches no tests fails the run, so filtering
/// this one out would turn it red. CI discovers these and they skip themselves for want of a
/// credential. Actually running them is the job of <c>integration.yml</c>, which is manual.
/// </para>
/// <para>
/// The key is read from <c>JEV_DOTNET_INTEGRATION_API_KEY</c>, not from <c>TYPESAFE_API_KEY</c>.
/// Every evaluation is billed, and a developer who has the ordinary variable set in their shell for
/// some other reason should not start spending money by running the test suite. Setting a variable
/// that exists only for this project is the opt-in.
/// </para>
/// <para>
/// The budget is fixed and small: three evaluations of a few hundred tokens each and one model
/// listing. Each evaluation settles one of the questions only an authenticated call can settle (the
/// readme lists them under Known Limitations) rather than re-proving what the offline suites
/// already cover. What needs no key — how a missing or wrong key is refused — is in
/// <see cref="KeylessLiveTests"/>. State and instructions are synthetic; nothing here should ever be
/// real data.
/// </para>
/// <para>
/// The client runs in <see cref="JevNumericalConsistency.Report"/> mode. How closely the service's
/// numbers keep the relationships the contract describes, and to how many digits, is one of the
/// things these tests measure: a strict check would stop at the first deviation and record none of
/// them. Each test writes what it saw to <see cref="LiveReport"/> — including, when the SDK or the
/// service refuses, the kind of refusal and the shape of the response — and asserts only what the
/// contract promises outright.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class LiveApiTests
{
    private const string KeyVariable = "JEV_DOTNET_INTEGRATION_API_KEY";

    private static string? Key => Environment.GetEnvironmentVariable(KeyVariable) is { Length: > 0 } key ? key : null;

    private static JevClient Client(string key, Recording recording)
    {
        var services = new ServiceCollection();
        services
            .AddJev(o =>
            {
                o.Credential = new StaticJevCredential(key);

                // Report, so a deviation becomes a warning the report records instead of a refusal
                // that hides every other number in the response.
                o.NumericalConsistency = JevNumericalConsistency.Report;

                // So the report can show each number exactly as the service spelled it.
                o.CaptureRawResponse = true;
            })
            .AddHttpMessageHandler(() => new RecordingHandler(recording));

        // The provider is not disposed: the client outlives this method, and the process ends
        // when the test run does.
        return services.BuildServiceProvider().GetRequiredService<JevClient>();
    }

    /// <summary>Makes the call; if the SDK or the service refuses it, reports how before the test fails.</summary>
    private static async Task<T> Observe<T>(string title, Recording recording, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (JevProtocolException ex)
        {
            LiveReport.Write(title, [$"**Refused by the SDK:** `{ex.Error}`", "Shape of the response: " + LiveReport.Shape(recording.LastBody)]);
            throw;
        }
        catch (JevHttpException ex)
        {
            LiveReport.Write(title, ["**Refused by the service.**", .. LiveReport.Describe(ex)]);
            throw;
        }
    }

    [Fact]
    public async Task ListsModels()
    {
        Assert.SkipWhen(Key is null, $"{KeyVariable} is not set; skipping live API tests.");

        const string Title = "GET /v1/models";
        var recording = new Recording();
        var models = await Observe(Title, recording, () => Client(Key!, recording).GetModelsAsync(cancellationToken: TestContext.Current.CancellationToken));

        LiveReport.Write(Title, [.. models.Select(m => $"`{m.Name}`, released {m.ReleaseDate}")]);

        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.False(string.IsNullOrWhiteSpace(m.Name)));
    }

    /// <summary>
    /// One call with every question kind, read back through the typed handles.
    /// </summary>
    /// <remarks>
    /// Not "the model answered correctly" — that is not the SDK's contract, and a probabilistic
    /// answer is not something to assert on. What is checked is that the service accepted the
    /// request as this SDK encodes it and that the response satisfied the reader, which refuses
    /// anything missing, duplicated or mistyped. The report adds what no assertion can: the
    /// precision of the numbers and how far each distribution strays from the answer beside it.
    /// </remarks>
    [Fact]
    public async Task EvaluatesAPlanWithEveryQuestionKind()
    {
        Assert.SkipWhen(Key is null, $"{KeyVariable} is not set; skipping live API tests.");

        const string Title = "POST /v1/systemone: every question kind";
        JevContent[] levels = ["Calm", "Annoyed", "Angry"];
        var builder = new JevDecisionPlanBuilder();
        var team = builder.AddChoice<string>("team", "Which team should handle this message?",
        [
            new("billing", "billing", "Payments, invoices and refunds"),
            new("technical", "technical", "Bugs, outages and integrations"),
        ]);
        var mood = builder.AddScore("mood", "How upset is the writer?", levels);
        var urgent = builder.AddNoul("urgent", "Does the writer need an answer today?");
        var plan = builder.Build();

        var recording = new Recording();
        var result = await Observe(Title, recording, () => Client(Key!, recording).EvaluateAsync(
            "I was charged twice for the same invoice this morning and need it reversed before my card statement closes tonight.",
            plan,
            cancellationToken: TestContext.Current.CancellationToken));

        LiveReport.Write(Title, LiveReport.Describe(result, new Dictionary<string, JevContent[]> { ["mood"] = levels }));

        Assert.Contains(result.Get(team).Value, new[] { "billing", "technical" });
        Assert.InRange(result.Get(mood).Value, 0, 2);
        Assert.Equal(3, result.Get(mood).Legend.Length);
        Assert.InRange(result.Get(urgent).Probability, 0, 1);
        Assert.True(result.Usage.InputTokens > 0);
        Assert.False(string.IsNullOrWhiteSpace(result.ActualModel));
        Assert.Equal(JevClientOptions.DefaultModelName, result.RequestedModel);
    }

    /// <summary>
    /// A state and score levels that are JSON rather than text.
    /// </summary>
    /// <remarks>
    /// The OpenAPI document lets a state and a level be text, an object or an array, and says a
    /// legend value can be any of the three; the HTTP reference shows only text. This is the call
    /// that shows whether the service takes structure as the document says, and what its legend
    /// gives back for levels that were objects.
    /// </remarks>
    [Fact]
    public async Task EvaluatesAStructuredStateAndStructuredLevels()
    {
        Assert.SkipWhen(Key is null, $"{KeyVariable} is not set; skipping live API tests.");

        const string Title = "POST /v1/systemone: a structured state and structured levels";
        JevContent[] levels =
        [
            JevContent.FromJson("""{"severity":"cosmetic","example":"a typo on a help page"}"""),
            JevContent.FromJson("""{"severity":"degraded","example":"checkout is slow but works"}"""),
            JevContent.FromJson("""["outage","customers cannot pay","no workaround"]"""),
        ];

        var builder = new JevDecisionPlanBuilder();
        var severity = builder.AddScore("severity", "How severe is the reported problem?", levels);
        var plan = builder.Build();
        var state = JevContent.FromJson("""{"channel":"email","subject":"Checkout fails","body":"Every payment attempt since noon ends on an error page."}""");

        var recording = new Recording();
        var result = await Observe(Title, recording, () => Client(Key!, recording).EvaluateContentAsync(state, plan, cancellationToken: TestContext.Current.CancellationToken));

        LiveReport.Write(Title, LiveReport.Describe(result, new Dictionary<string, JevContent[]> { ["severity"] = levels }));

        Assert.InRange(result.Get(severity).Value, 0, 2);
        Assert.Equal(3, result.Get(severity).Legend.Length);
    }

    /// <summary>
    /// A question with no instructions is accepted.
    /// </summary>
    /// <remarks>
    /// The OpenAPI document makes <c>instructions</c> optional, and the HTTP reference says it is
    /// required. This SDK follows the document — <c>instructions-optional</c>
    /// in <c>spec/typesafe-v1/semantics.json</c> — and this is the call that settles whether the
    /// service agrees. If it does not, the report records the status and the validation error's
    /// location before the test fails, and the readme's statement that omission is accepted has to
    /// change.
    /// </remarks>
    [Fact]
    public async Task AQuestionWithoutInstructionsIsAccepted()
    {
        Assert.SkipWhen(Key is null, $"{KeyVariable} is not set; skipping live API tests.");

        const string Title = "POST /v1/systemone: a question without instructions";
        var builder = new JevDecisionPlanBuilder();
        var positive = builder.AddNoul("positive", default, new JevNoulCriteria("The message is positive", "The message is not positive"));
        var plan = builder.Build();

        var recording = new Recording();
        var result = await Observe(Title, recording, () => Client(Key!, recording).EvaluateAsync("Thanks, that fixed it!", plan, cancellationToken: TestContext.Current.CancellationToken));

        LiveReport.Write(Title, ["Accepted.", .. LiveReport.Describe(result, new Dictionary<string, JevContent[]>())]);

        Assert.InRange(result.Get(positive).Probability, 0, 1);
    }
}
