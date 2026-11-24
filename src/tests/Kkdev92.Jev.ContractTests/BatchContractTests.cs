using System.Net;
using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.Time.Testing;
using static Kkdev92.Jev.ContractTests.Clients;

namespace Kkdev92.Jev.ContractTests;

public sealed class BatchContractTests
{
    [Theory]
    [InlineData(false, false, JevRequestTransmission.Started)]
    [InlineData(true, false, JevRequestTransmission.Unknown)]
    [InlineData(true, true, JevRequestTransmission.Unknown)]
    public async Task TransmissionIncludesEveryAttempt(bool unknownFirst, bool success, JevRequestTransmission expected)
    {
        var handler = FakeHttpMessageHandler.Sequence(
            () => unknownFirst ? throw new HttpRequestException("private-sentinel") : FakeResponses.Json("{}", HttpStatusCode.ServiceUnavailable),
            () => success ? FakeResponses.Json(Answer()) : throw new HttpRequestException(HttpRequestError.ConnectionError, "private-sentinel"));
        var client = Create(handler, o => { o.AdditionalRetries = 1; o.RetryNetworkFailures = true; });
        var result = await client.EvaluateManyAsync(["text"], Plan().Plan,
            new() { FailureMode = JevBatchFailureMode.Collect }, cancellationToken: TestContext.Current.CancellationToken);
        var item = Assert.Single(result.Items);
        Assert.Equal(expected, item.Transmission);
        Assert.Equal(2, handler.Requests.Count);
        if (success)
        {
            Assert.NotNull(item.Result);
        }
        else
        {
            Assert.Equal(JevRequestTransmission.NotStarted, Assert.IsType<JevTransportException>(item.Error).Transmission);
        }
    }

    [Fact]
    public async Task CollectRetainsInputOrderAndValidatesBeforeSending()
    {
        var handler = FakeHttpMessageHandler.Sequence(() => FakeResponses.Json(Answer()), () => FakeResponses.Json("{}", HttpStatusCode.UnprocessableEntity),
            () => FakeResponses.Json(Answer()));
        var client = Create(handler, o => o.ConcurrencyLimit = 1);
        var plan = Plan().Plan;
        var options = new JevBatchOptions { FailureMode = JevBatchFailureMode.Collect };
        var token = TestContext.Current.CancellationToken;
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.EvaluateManyAsync(["a", "\ud800"], plan, options, cancellationToken: token));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => client.EvaluateManyContentAsync(["a", default], plan, options, cancellationToken: token));
        Assert.Empty(handler.Requests);
        var result = await client.EvaluateManyContentAsync(["a", "b", "c"], plan, options, cancellationToken: token);
        Assert.Equal([0, 1, 2], result.Items.Select(i => i.Index));
        Assert.Equal(2, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(JevBatchItemStatus.Failed, result.Items[1].Status);
        Assert.Equal(422, Assert.IsType<JevHttpException>(result.Items[1].Error).StatusCode);
        Assert.All(result.Items, item => Assert.Equal(JevRequestTransmission.Started, item.Transmission));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopPreservesCompletedAndPendingResults(bool canceled)
    {
        var clock = new FakeTimeProvider();
        using var source = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var handler = new FakeHttpMessageHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
            return FakeResponses.Json(Answer());
        });
        var client = Create(handler, o => { o.TimeProvider = clock; o.ConcurrencyLimit = 1; });
        var plan = Plan().Plan;
        var run = client.EvaluateManyAsync(["a", "b", "c"], plan, new() { OverallTimeout = TimeSpan.FromSeconds(1) }, cancellationToken: source.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        JevBatchResult snapshot;
        if (canceled)
        {
            source.Cancel();
            var ex = await Assert.ThrowsAsync<JevBatchCanceledException>(() => run);
            Assert.Equal(source.Token, ex.CancellationToken);
            snapshot = ex.Snapshot;
        }
        else
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            snapshot = (await Assert.ThrowsAsync<JevBatchTimeoutException>(() => run)).Snapshot;
        }

        Assert.Equal([JevBatchItemStatus.Succeeded, JevBatchItemStatus.Canceled, JevBatchItemStatus.NotStarted], snapshot.Items.Select(i => i.Status));
        Assert.Equal([JevRequestTransmission.Started, JevRequestTransmission.Unknown, JevRequestTransmission.NotStarted], snapshot.Items.Select(i => i.Transmission));
        Assert.Equal(2, count);
        await client.EvaluateAsync("recovered", plan, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BatchSharesAdmissionWithASingleCallAndRecoversAfterARetriedFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        var handler = new FakeHttpMessageHandler(async (_, token) =>
        {
            switch (Interlocked.Increment(ref sends))
            {
                case 1:
                    entered.SetResult();
                    await release.Task.WaitAsync(token);
                    break;
                case 3:
                    return FakeResponses.Json("{}", HttpStatusCode.ServiceUnavailable);
                case 4:
                    throw new HttpRequestException(HttpRequestError.ConnectionError, "private-sentinel");
            }
            return FakeResponses.Json(Answer());
        });
        var client = Create(handler, o => { o.ConcurrencyLimit = 1; o.MaxQueuedRequests = 1; o.AdditionalRetries = 1; });
        var plan = Plan().Plan;
        var token = TestContext.Current.CancellationToken;
        var first = client.EvaluateAsync("single", plan, cancellationToken: token);
        await entered.Task.WaitAsync(token);
        var batch = client.EvaluateManyAsync(["ok", "retry"], plan, new() { MaxConcurrency = 1 }, cancellationToken: token);
        Assert.False(batch.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref sends));
        release.SetResult();
        await first;
        var failure = await Assert.ThrowsAsync<JevBatchException>(() => batch);
        Assert.Equal([JevBatchItemStatus.Succeeded, JevBatchItemStatus.Failed], failure.Snapshot.Items.Select(i => i.Status));
        Assert.Equal(JevRequestTransmission.Started, failure.Snapshot.Items[1].Transmission);
        Assert.Equal(JevRequestTransmission.NotStarted, Assert.IsType<JevTransportException>(failure.Snapshot.Items[1].Error).Transmission);
        Assert.DoesNotContain("private-sentinel", failure.ToString(), StringComparison.Ordinal);
        await client.EvaluateAsync("next", plan, cancellationToken: token);
        Assert.Equal(5, sends);
    }

    [Fact]
    public async Task UnexpectedErrorsStopCollectionAndNeverExpandIntoWrapperText()
    {
        var handler = FakeHttpMessageHandler.Sequence(() => throw new InvalidOperationException("private-sentinel"));
        var client = Create(handler);
        var ex = await Assert.ThrowsAsync<JevBatchException>(() => client.EvaluateManyAsync(["a", "b"], Plan().Plan,
            new() { FailureMode = JevBatchFailureMode.Collect }, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(0, ex.FailedIndex);
        Assert.Equal(1, ex.Snapshot.NotStartedCount);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("private-sentinel", ex.ToString(), StringComparison.Ordinal);
        Assert.Equal("private-sentinel", ex.Snapshot.Items[0].Error?.Message);
    }
}
