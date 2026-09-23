using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Kkdev92.Jev.TestSupport;
using static Kkdev92.Jev.ContractTests.Clients;

namespace Kkdev92.Jev.ContractTests;

/// <summary>
/// Everything a caller sends, and the server's own text, carries a marker, and no output the SDK
/// produces may repeat one: not an exception's message or its <c>ToString()</c>, not the call's
/// activity, not a metric tag — after a success, and after each kind of failure.
/// </summary>
/// <remarks>
/// A number cannot carry a marker, so the answers are covered through their labels: the choice
/// answered here is a marked label, and the score's legend echoes marked levels. The server's
/// machine-readable codes are left unmarked on purpose: a code shaped like an identifier may appear
/// in a message, and that is the design. A handle and a consistency warning name their question:
/// they are the caller's own values, not diagnostics, and are not looked at here. The listeners are
/// process-wide, so this shares the telemetry collection rather than running beside it.
/// </remarks>
[Collection(nameof(TelemetryContractTests))]
public sealed class PrivacyContractTests
{
    private const string Key = "sk-privacy-marker-key";
    private const string State = "privacy-marker-state";
    private const string ChoiceId = "privacy-marker-choice-id";
    private const string ScoreId = "privacy-marker-score-id";
    private const string NoulId = "privacy-marker-noul-id";
    private const string Instructions = "privacy-marker-instructions";
    private const string LabelA = "privacy-marker-label-a";
    private const string LabelB = "privacy-marker-label-b";
    private const string Description = "privacy-marker-description";
    private const string LevelLow = "privacy-marker-level-low";
    private const string LevelHigh = "privacy-marker-level-high";
    private const string WhenTrue = "privacy-marker-when-true";
    private const string WhenFalse = "privacy-marker-when-false";
    private const string ModelAsked = "privacy-marker-model-asked";
    private const string ModelAnswered = "privacy-marker-model-answered";
    private const string ServerText = "privacy-marker-server-text";
    private const string UnaskedId = "privacy-marker-unasked-id";
    private const string UnknownLabel = "privacy-marker-unknown-label";

    private static readonly string[] Markers =
    [
        Key, State, ChoiceId, ScoreId, NoulId, Instructions, LabelA, LabelB, Description, LevelLow, LevelHigh,
        WhenTrue, WhenFalse, ModelAsked, ModelAnswered, ServerText, UnaskedId, UnknownLabel,
    ];

    private static JevDecisionPlan MarkedPlan()
    {
        var builder = new JevDecisionPlanBuilder();
        builder.AddChoice<int>(ChoiceId, Instructions, [new(1, LabelA, Description), new(2, LabelB)]);
        builder.AddScore(ScoreId, Instructions, [LevelLow, LevelHigh]);
        builder.AddNoul(NoulId, Instructions, new JevNoulCriteria(WhenTrue, WhenFalse));
        return builder.Build();
    }

    private static SystemOneResponseBuilder Answers(bool withNoul = true)
    {
        var answers = new SystemOneResponseBuilder()
            .Model(ModelAnswered)
            .Choice(ChoiceId, LabelA, 0.5, (LabelA, 0.8), (LabelB, 0.2))
            .Score(ScoreId, 0.25, 0.5, [LevelLow, LevelHigh], 0.75, 0.25);

        return withNoul ? answers.Noul(NoulId, 0.9) : answers;
    }

    /// <summary>Each response, and whether the call is expected to succeed.</summary>
    private static (string Name, Func<HttpResponseMessage> Response, bool Strict)[] Scenarios() =>
    [
        ("success", () => FakeResponses.Json(Answers().Build()), false),
        ("a warning", () => FakeResponses.Json(new SystemOneResponseBuilder().Model(ModelAnswered)
            .Choice(ChoiceId, LabelA, 0.5, (LabelA, 0.7), (LabelB, 0.1))
            .Score(ScoreId, 0.25, 0.5, [LevelLow, LevelHigh], 0.75, 0.25)
            .Noul(NoulId, 0.9).Build()), false),
        ("strict refusal", () => FakeResponses.Json(new SystemOneResponseBuilder().Model(ModelAnswered)
            .Choice(ChoiceId, LabelA, 0.5, (LabelA, 0.7), (LabelB, 0.1))
            .Score(ScoreId, 0.25, 0.5, [LevelLow, LevelHigh], 0.75, 0.25)
            .Noul(NoulId, 0.9).Build()), true),
        ("422", () => FakeResponses.Json(
            $$"""{"detail":[{"loc":["body","questions","{{ChoiceId}}","criteria","{{LabelA}}"],"msg":"{{ServerText}}","type":"string_too_long","input":"{{State}}"}]}""",
            HttpStatusCode.UnprocessableEntity), false),
        ("401", () => FakeResponses.Json($$$"""{"detail":{"error_type":"authentication_error","message":"{{{ServerText}}}"}}""", HttpStatusCode.Unauthorized), false),
        ("403", () => FakeResponses.Json($$$"""{"detail":{"error_type":"authentication_error","message":"{{{ServerText}}}"}}""", HttpStatusCode.Forbidden), false),
        ("429", () => FakeResponses.Json($$"""{"detail":"{{ServerText}}"}""", HttpStatusCode.TooManyRequests), false),
        ("529", () => FakeResponses.Json($$"""{"detail":"{{ServerText}}"}""", (HttpStatusCode)529), false),
        ("500", () => FakeResponses.Json($$"""{"detail":"{{ServerText}} {{State}}"}""", HttpStatusCode.InternalServerError), false),
        ("unknown label", () => FakeResponses.Json(new SystemOneResponseBuilder().Model(ModelAnswered)
            .Choice(ChoiceId, LabelA, 0.5, (LabelA, 0.8), (LabelB, 0.1), (UnknownLabel, 0.1))
            .Score(ScoreId, 0.25, 0.5, [LevelLow, LevelHigh], 0.75, 0.25)
            .Noul(NoulId, 0.9).Build()), false),
        ("unasked answer", () => FakeResponses.Json(Answers(withNoul: false).Noul(UnaskedId, 0.9).Build()), false),
        ("unknown type", () => FakeResponses.Json(Answers(withNoul: false).Raw(NoulId, $$"""{"type":"{{ServerText}}","noul":0.9}""").Build()), false),
        ("missing usage", () => FakeResponses.Json(Answers().Usage("""{"input_tokens":1}""").Build()), false),
        ("malformed", () => FakeResponses.Json($$"""{"model":"{{ModelAnswered}}","answers":{"{{ChoiceId}}":{"type":"choice","choice":"{{LabelA}}" """), false),
    ];

    [Fact]
    public async Task NoOutputRepeatsAnythingTheCallerSentOrTheServerSaid()
    {
        var activities = new ConcurrentBag<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == JevDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };
        ActivitySource.AddActivityListener(activityListener);

        var metricTags = new ConcurrentBag<string>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == JevDiagnostics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<double>((_, _, tags, _) => Record(metricTags, tags));
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) => Record(metricTags, tags));
        meterListener.SetMeasurementEventCallback<int>((_, _, tags, _) => Record(metricTags, tags));
        meterListener.Start();

        var plan = MarkedPlan();
        var outputs = new List<(string Where, string Text)>();
        var succeeded = 0;
        var warnings = 0;

        foreach (var (name, response, strict) in Scenarios())
        {
            var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(response()));
            var client = Create(handler, o =>
            {
                o.Credential = new StaticJevCredential(Key);
                o.NumericalConsistency = strict ? JevNumericalConsistency.Strict : JevNumericalConsistency.Report;
            });

            try
            {
                var result = await client.EvaluateAsync(State, plan, new JevRequestOptions { Model = ModelAsked }, TestContext.Current.CancellationToken);
                succeeded++;
                warnings += result.Metadata.Warnings.Count;

                // Besides its size, the one thing a result prints is the model that answered.
                Assert.Equal($"JevResult (3 answers from {ModelAnswered})", result.ToString());
            }
            catch (JevException exception)
            {
                outputs.Add(($"{name}: message", exception.Message));
                outputs.Add(($"{name}: ToString()", exception.ToString()));
            }
        }

        meterListener.RecordObservableInstruments();

        foreach (var activity in activities.Where(a => a.OperationName.StartsWith("Jev.", StringComparison.Ordinal)))
        {
            outputs.Add(("activity name", activity.DisplayName));
            outputs.Add(("activity status", activity.StatusDescription ?? string.Empty));
            outputs.AddRange(activity.TagObjects.Select(t => ($"activity tag {t.Key}", t.Value?.ToString() ?? string.Empty)));
            outputs.AddRange(activity.Events.Select(e => ("activity event", e.Name)));
            outputs.AddRange(activity.Baggage.Select(b => ($"activity baggage {b.Key}", b.Value ?? string.Empty)));
        }

        outputs.AddRange(metricTags.Select(t => ("metric tag", t)));

        // The check is only as good as what it saw: two successes, one of them with a warning, every
        // failure, and an activity for each.
        Assert.Equal(2, succeeded);
        Assert.True(warnings > 0, "The response meant to raise a consistency warning raised none.");
        Assert.True(activities.Count >= Scenarios().Length, $"Only {activities.Count} activities were recorded.");
        Assert.Contains(metricTags, t => t.Length > 0);

        var leaks = outputs
            .SelectMany(o => Markers.Where(m => o.Text.Contains(m, StringComparison.Ordinal)).Select(m => $"{o.Where} repeats '{m}': {o.Text}"))
            .ToList();

        Assert.True(leaks.Count == 0, string.Join(Environment.NewLine, leaks));
    }

    private static void Record(ConcurrentBag<string> into, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            into.Add($"{tag.Key}={tag.Value}");
        }
    }
}
