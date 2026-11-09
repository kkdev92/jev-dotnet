using Kkdev92.Jev.DependencyInjection;
using Kkdev92.Jev.TestSupport;
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
/// The budget is fixed and small: seven small evaluations — three of them showing a small synthetic
/// image — one model listing, and one evaluation with a state too long for the default context,
/// which the service refuses.
/// TypeSafe does not say whether a refused request is billed; if it is, that one is most of a run.
/// Each evaluation settles one of the questions only an authenticated call can settle (the readme
/// lists them under Known Limitations) rather than re-proving what the offline suites already
/// cover. What needs no key — how a missing or wrong key is refused — is in
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

    // Spelled out rather than taken from the SDK, so the tests do not check the SDK against itself.
    private const string LongContextHeader = "TypeSafe-Danger-Zone-Long-Context";
    private const string ImagesHeader = "TypeSafe-Danger-Zone-Images";

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
            LiveReport.Write(title, ["**Refused by the service.**", .. LiveReport.Describe(ex), "Shape of the response: " + LiveReport.Shape(recording.LastBody)]);
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

    /// <summary>
    /// An evaluation that asks for long context is accepted.
    /// </summary>
    /// <remarks>
    /// TypeSafe's contract declares the long-context header and its models page says what it does;
    /// only a keyed call shows that the service takes the header as this SDK sends it. The
    /// evaluation is a small one, far inside the default limits: it shows that asking is accepted
    /// and the answer reads as usual, not that the limits rise. Showing that would take a state
    /// longer than the default context, and it would be evaluated and billed in full.
    /// </remarks>
    [Fact]
    public async Task AnEvaluationAskingForLongContextIsAccepted()
    {
        Assert.SkipWhen(Key is null, $"{KeyVariable} is not set; skipping live API tests.");

        const string Title = "POST /v1/systemone: asking for long context";
        var builder = new JevDecisionPlanBuilder();
        var positive = builder.AddNoul("positive", "Is the message positive?");
        var plan = builder.Build();

        var recording = new Recording();
        var result = await Observe(Title, recording, () => Client(Key!, recording).EvaluateAsync(
            "Thanks, that fixed it!",
            plan,
            new JevRequestOptions { UseLongContext = true },
            TestContext.Current.CancellationToken));

        LiveReport.Write(Title, ["Accepted.", .. LiveReport.Describe(result, new Dictionary<string, JevContent[]>())]);

        Assert.Contains(LongContextHeader, Assert.Single(recording.HeaderNames), StringComparer.OrdinalIgnoreCase);
        Assert.InRange(result.Get(positive).Probability, 0, 1);
    }

    /// <summary>
    /// A state that shows an image is accepted, sent as a form.
    /// </summary>
    /// <remarks>
    /// TypeSafe documents images as experimental: a form whose first part is the request, a
    /// reference in the state standing for each image, and a header that turns images on. Only a
    /// keyed call shows that the service takes the form as this SDK writes it — each image a file
    /// part with a filename and its media type. The image is made by the test: a black square on
    /// white. Whether the model sees the square is reported, not asserted.
    /// </remarks>
    [Fact]
    public async Task AStateThatShowsAnImageIsAccepted()
    {
        Assert.SkipWhen(Key is null, $"{KeyVariable} is not set; skipping live API tests.");

        const string Title = "POST /v1/systemone: an image in the state";
        var photo = JevImage.FromBytes(TestImages.Png(320, 320, (32, 32, 96)));
        var builder = new JevDecisionPlanBuilder();
        var square = builder.AddNoul("square", "Is there a black square in `photo`?");
        var plan = builder.Build();
        var state = JevContent.FromJson($$"""{"photo":"{{photo.Reference}}"}""");

        var recording = new Recording();
        var result = await Observe(Title, recording, () => Client(Key!, recording).EvaluateContentAsync(state, [photo], plan, cancellationToken: TestContext.Current.CancellationToken));

        LiveReport.Write(Title, ["Accepted.", .. LiveReport.Describe(result, new Dictionary<string, JevContent[]>())]);

        Assert.Contains(ImagesHeader, Assert.Single(recording.HeaderNames), StringComparer.OrdinalIgnoreCase);
        Assert.InRange(result.Get(square).Probability, 0, 1);
    }

    /// <summary>
    /// A box question about an image is answered with four edges.
    /// </summary>
    /// <remarks>
    /// The square covers 0.1 to 0.4 of the image's width and of its height. Where the model puts the
    /// box is reported beside that, not asserted: TypeSafe says the accuracy of box answers will
    /// change as it updates its models. What is asserted is what the contract promises — four edges
    /// from 0 to 1, which the SDK checked as it read them, and a confidence from 0 to 1 when there is
    /// one.
    /// </remarks>
    [Fact]
    public async Task ABoxQuestionIsAnsweredWithFourEdges()
    {
        Assert.SkipWhen(Key is null, $"{KeyVariable} is not set; skipping live API tests.");

        const string Title = "POST /v1/systemone: a box question";
        var photo = JevImage.FromBytes(TestImages.Png(320, 320, (32, 32, 96)));
        var builder = new JevDecisionPlanBuilder();
        var square = builder.AddBox("square", "the black square in `photo`");
        var plan = builder.Build();
        var state = JevContent.FromJson($$"""{"photo":"{{photo.Reference}}"}""");

        var recording = new Recording();
        var result = await Observe(Title, recording, () => Client(Key!, recording).EvaluateContentAsync(state, [photo], plan, cancellationToken: TestContext.Current.CancellationToken));

        LiveReport.Write(Title, ["Accepted. The square covers top 0.1, bottom 0.4, left 0.1, right 0.4.", .. LiveReport.Describe(result, new Dictionary<string, JevContent[]>())]);

        var box = result.Get(square);
        Assert.Contains(ImagesHeader, Assert.Single(recording.HeaderNames), StringComparer.OrdinalIgnoreCase);
        Assert.All([box.Top, box.Bottom, box.Left, box.Right], edge => Assert.InRange(edge, 0, 1));

        if (box.Confidence is { } confidence)
        {
            Assert.InRange(confidence, 0, 1);
        }
    }

    /// <summary>
    /// An image a question shows and an image the state shows are accepted together.
    /// </summary>
    /// <remarks>
    /// TypeSafe's documentation puts a target image's reference directly in a question's
    /// instructions, and refuses an image that nothing in the request references. This is the call
    /// that shows a reference in the instructions counts — the plan's image is referenced nowhere
    /// else — with the plan's image sent first and the state's after it.
    /// </remarks>
    [Fact]
    public async Task AnImageInTheInstructionsAndOneInTheStateAreAcceptedTogether()
    {
        Assert.SkipWhen(Key is null, $"{KeyVariable} is not set; skipping live API tests.");

        const string Title = "POST /v1/systemone: an image in the instructions and one in the state";
        var card = JevImage.FromBytes(TestImages.Png(320, 320, (32, 32, 96)));
        var photo = JevImage.FromBytes(TestImages.Png(320, 320, (192, 160, 96)));
        var builder = new JevDecisionPlanBuilder();
        var same = builder.AddNoul("same", $"Is the shape in `photo` the same as the shape in this picture: {card.Reference}?");
        builder.AddImage(card);
        var plan = builder.Build();
        var state = JevContent.FromJson($$"""{"photo":"{{photo.Reference}}"}""");

        var recording = new Recording();
        var result = await Observe(Title, recording, () => Client(Key!, recording).EvaluateContentAsync(state, [photo], plan, cancellationToken: TestContext.Current.CancellationToken));

        LiveReport.Write(Title, ["Accepted.", .. LiveReport.Describe(result, new Dictionary<string, JevContent[]>())]);

        Assert.Contains(ImagesHeader, Assert.Single(recording.HeaderNames), StringComparer.OrdinalIgnoreCase);
        Assert.InRange(result.Get(same).Probability, 0, 1);
    }

    /// <summary>
    /// A state too long for the default context is refused, without asking for long context.
    /// </summary>
    /// <remarks>
    /// TypeSafe documents a <c>400</c> with <c>max_tokens_exceeded</c> for a request longer than the
    /// model's context length, without saying where in the body the type appears. It has been seen
    /// as <c>detail.error_type</c>, which is where the SDK reads it (<c>status-400</c> in
    /// <c>spec/typesafe-v1/semantics.json</c>), and this is the call that shows it is still there.
    /// If it moves, the report records the shape of the body before the test fails. The state is one
    /// short word repeated — at about a token each, longer than the default per-question context and
    /// shorter than the per-request one.
    /// </remarks>
    [Fact]
    public async Task AStateTooLongForTheDefaultContextIsRefused()
    {
        Assert.SkipWhen(Key is null, $"{KeyVariable} is not set; skipping live API tests.");

        const string Title = "POST /v1/systemone: a state too long for the default context";
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("billing", "Is the text about billing?");
        var plan = builder.Build();
        var state = string.Concat(Enumerable.Repeat(" the", 45_000));

        var recording = new Recording();
        var client = Client(Key!, recording);
        var refusal = await Assert.ThrowsAsync<JevHttpException>(async () =>
        {
            var result = await Observe(Title, recording, () => client.EvaluateAsync(state, plan, cancellationToken: TestContext.Current.CancellationToken));
            LiveReport.Write(Title, ["**Accepted**, although TypeSafe documents a refusal.", .. LiveReport.Describe(result, new Dictionary<string, JevContent[]>())]);
        });

        Assert.DoesNotContain(LongContextHeader, Assert.Single(recording.HeaderNames), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(400, refusal.StatusCode);
        Assert.Equal("max_tokens_exceeded", refusal.ErrorType);
    }
}
