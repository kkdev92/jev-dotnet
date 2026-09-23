using System.Text;
using Kkdev92.Jev.TestSupport;

namespace Kkdev92.Jev.IntegrationTests;

/// <summary>
/// The report the live tests write, checked offline against a fake of the service.
/// </summary>
/// <remarks>
/// A keyed run costs money, and a bug in the reporting would waste what it observed. So the
/// reporting is tested here, on every CI run, rather than there for the first time.
/// </remarks>
public sealed class LiveReportTests
{
    [Fact]
    public async Task AnEvaluationIsDescribedNumberByNumberAsSpelled()
    {
        JevContent[] levels = ["Calm", "Annoyed", "Angry"];
        var builder = new JevDecisionPlanBuilder();
        builder.AddChoice<string>("team", "q", [new("billing", "billing"), new("technical", "technical")]);
        builder.AddScore("mood", "q", levels);
        builder.AddNoul("urgent", "q");
        var plan = builder.Build();

        var body = new SystemOneResponseBuilder()
            .Choice("team", "technical", 0.81, ("billing", 0.88), ("technical", 0.12))
            .Score("mood", 1.05, 0.92, ["Calm", "Annoyed", "Irate"], 0.0, 0.95, 0.05)
            .Raw("urgent", """{"type":"noul","noul":0.950}""")
            .Build();

        var result = await Client(body).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);
        var lines = LiveReport.Describe(result, new Dictionary<string, JevContent[]> { ["mood"] = levels });

        Assert.Contains("Model: requested `jev-latest`, answered by `jev-1.13.0`", lines);
        Assert.Contains("Request id: `req_` and 32 hex digits", lines);
        Assert.Contains(lines, l => l.StartsWith("SDK consistency warnings at the default tolerance: `team` ChoiceIsMostProbable 0.76", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("`team` (choice): `technical`, confidence 0.81; `billing` 0.88, `technical` 0.12; sum - 1 = ", StringComparison.Ordinal)
            && l.Contains("chosen is 0.76", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("`mood` (score): 1.05, confidence 0.92; 0: 0, 1: 0.95, 2: 0.05;", StringComparison.Ordinal)
            && l.EndsWith("legend (string, string, string) **differs from the levels sent**", StringComparison.Ordinal));
        Assert.Contains("`urgent` (noul): 0.950", lines);
        Assert.Contains("Numbers in the answers: 9; most digits after the point: 3; exponent notation: no", lines);
    }

    [Fact]
    public void TheShapeOfARefusedBodyNamesKindsAndNeverValues()
    {
        var body = Encoding.UTF8.GetBytes("""{"model":"a-model-name","answers":{"q":{"type":"noul","noul":0.5,"notes":["x"]}},"usage":{"input_tokens":null}}""");

        Assert.Equal("`{model: string, answers: {q: {type: \"noul\", noul: number, notes: array(1)}}, usage: {input_tokens: null}}`", LiveReport.Shape(body));
        Assert.Equal("not JSON (3 bytes)", LiveReport.Shape("{x}"u8.ToArray()));
        Assert.Equal("(no body recorded)", LiveReport.Shape(null));
    }

    [Fact]
    public async Task ARefusalIsDescribedWithoutWhatTheServiceSaid()
    {
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.ValidationFailed()));
        var client = new JevClient(new HttpClient(handler), new JevClientOptions { Credential = new StaticJevCredential("sk-test") });
        var builder = new JevDecisionPlanBuilder();
        builder.AddNoul("q", "q");

        var exception = await Assert.ThrowsAsync<JevHttpException>(() => client.EvaluateAsync("text", builder.Build(), cancellationToken: TestContext.Current.CancellationToken));
        var lines = LiveReport.Describe(exception);

        Assert.Contains("HTTP 422, error type `(none)`, request Started", lines);
        Assert.Contains("Validation errors: `too_short` at `body.questions.urgency.criteria`; `missing` at `body.state`", lines);
        Assert.DoesNotContain(lines, l => l.Contains("SECRET", StringComparison.Ordinal) || l.Contains("Field required", StringComparison.Ordinal));
    }

    private static JevClient Client(string body)
        => new(new HttpClient(FakeHttpMessageHandler.Always(body)), new JevClientOptions
        {
            Credential = new StaticJevCredential("sk-test"),
            CaptureRawResponse = true,
        });
}
