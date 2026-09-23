using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Kkdev92.Jev.Diagnostics;
using Kkdev92.Jev.TestSupport;
using static Kkdev92.Jev.ContractTests.Clients;

namespace Kkdev92.Jev.ContractTests;

/// <summary>
/// What the SDK tells a listener, and what it never does. Tests in this class share the process-wide
/// source, so they filter by a marker of their own and do not run in parallel with each other.
/// </summary>
[Collection(nameof(TelemetryContractTests))]
public sealed class TelemetryContractTests
{
    private const string Secret = "state-secret-telemetry-5555";

    [Fact]
    public async Task ACallIsOneInternalActivityWithOnlyAllowlistedTags()
    {
        var activities = new ConcurrentBag<Activity>();
        using var listener = Listen(activities);
        var (plan, _, _) = Plan();

        using (var parent = new Activity("test-parent").Start())
        {
            await Create(FakeHttpMessageHandler.Always(Answer())).EvaluateAsync(Secret, plan, cancellationToken: TestContext.Current.CancellationToken);
        }

        var activity = Assert.Single(activities, a => a.OperationName == "Jev.Evaluate" && a.ParentId is not null);

        Assert.Equal(ActivityKind.Internal, activity.Kind);
        Assert.Equal(JevDiagnostics.ActivitySourceName, activity.Source.Name);
        Assert.All(activity.TagObjects, tag => Assert.Contains(tag.Key, JevTelemetry.AllTags));
        Assert.Equal("evaluate", activity.GetTagItem(JevTelemetry.OperationTag));
        Assert.Equal("success", activity.GetTagItem(JevTelemetry.OutcomeTag));
        Assert.Equal(1, activity.GetTagItem(JevTelemetry.AttemptsTag));
        Assert.Equal(200, activity.GetTagItem(JevTelemetry.StatusCodeTag));
        Assert.Equal(120L, activity.GetTagItem(JevTelemetry.InputTokensTag));
        Assert.Equal(FakeResponses.RequestId, activity.GetTagItem(JevTelemetry.RequestIdTag));
        AssertNothingSensitive(activity);
    }

    [Fact]
    public async Task AFailedCallRecordsItsOutcomeAndErrorTypeButNotItsMessage()
    {
        var activities = new ConcurrentBag<Activity>();
        using var listener = Listen(activities);
        var (plan, _, _) = Plan();

        using (new Activity("test-parent").Start())
        {
            await Assert.ThrowsAsync<JevHttpException>(() => Create(new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.ValidationFailed(Secret))))
                .EvaluateAsync(Secret, plan, cancellationToken: TestContext.Current.CancellationToken));
        }

        var activity = Assert.Single(activities, a => a.OperationName == "Jev.Evaluate" && a.ParentId is not null);

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("http_error", activity.StatusDescription);
        Assert.Equal("http_error", activity.GetTagItem(JevTelemetry.OutcomeTag));
        Assert.Equal(typeof(JevHttpException).FullName, activity.GetTagItem(JevTelemetry.ErrorTypeTag));
        Assert.Equal(422, activity.GetTagItem(JevTelemetry.StatusCodeTag));
        AssertNothingSensitive(activity);
    }

    [Fact]
    public async Task MetricsCarryOnlyTheOperationOutcomeAndTokenType()
    {
        var measurements = new ConcurrentBag<(string Instrument, object Value, KeyValuePair<string, object?>[] Tags)>();
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

        meterListener.SetMeasurementEventCallback<double>((i, v, t, _) => measurements.Add((i.Name, v, t.ToArray())));
        meterListener.SetMeasurementEventCallback<long>((i, v, t, _) => measurements.Add((i.Name, v, t.ToArray())));
        meterListener.Start();

        var (plan, _, _) = Plan();
        await Create(FakeHttpMessageHandler.Always(Answer())).EvaluateAsync(Secret, plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(measurements, m => m.Instrument == "jev.client.operation.duration");
        Assert.Contains(measurements, m => m.Instrument == "jev.client.token.usage" && (long)m.Value == 120);

        foreach (var (_, _, tags) in measurements)
        {
            Assert.All(tags, tag => Assert.Contains(tag.Key, new[] { JevTelemetry.OperationTag, JevTelemetry.OutcomeTag, JevTelemetry.TokenTypeTag }));
            Assert.All(tags, tag => Assert.DoesNotContain(Secret, tag.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));
        }
    }

    private static ActivityListener Listen(ConcurrentBag<Activity> activities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == JevDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void AssertNothingSensitive(Activity activity)
    {
        foreach (var (key, value) in activity.TagObjects)
        {
            var text = value?.ToString() ?? string.Empty;
            Assert.DoesNotContain(Secret, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ApiKey, text, StringComparison.Ordinal);
            Assert.DoesNotContain("tone", text, StringComparison.Ordinal);
            Assert.DoesNotContain("jev-latest", text, StringComparison.Ordinal);
            Assert.False(string.IsNullOrEmpty(key));
        }

        Assert.DoesNotContain(Secret, activity.DisplayName, StringComparison.Ordinal);
    }
}

[CollectionDefinition(nameof(TelemetryContractTests), DisableParallelization = true)]
public sealed class TelemetryCollection;
