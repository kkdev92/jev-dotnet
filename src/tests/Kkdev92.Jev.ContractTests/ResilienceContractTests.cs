using System.Net;
using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.Time.Testing;
using static Kkdev92.Jev.ContractTests.Clients;

namespace Kkdev92.Jev.ContractTests;

/// <summary>Retry, the deadline, cancellation and admission, driven by a fake clock.</summary>
public sealed class ResilienceContractTests
{
    [Fact]
    public async Task NothingIsRetriedByDefault()
    {
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Sequence(() => FakeResponses.RateLimited("0"), () => FakeResponses.Json(Answer()));

        await Assert.ThrowsAsync<JevHttpException>(() => Create(handler).EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ARetrySendsTheSameBytesInANewRequestWithAFreshKey()
    {
        var (plan, _, urgent) = Plan();
        var handler = FakeHttpMessageHandler.Sequence(FakeResponses.Overloaded, () => FakeResponses.RateLimited("0"), () => FakeResponses.Json(Answer(urgent: 0.42)));
        var credential = new ErrorContractTests.DelegateCredential(call => $"sk-rotated-{call}");

        var result = await Create(handler, o => { o.AdditionalRetries = 2; o.Credential = credential; })
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(0.42, result.Get(urgent).Probability);
        Assert.Equal(3, result.Metadata.Attempts);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal(handler.Requests[0].Body, r.Body));
        Assert.Equal(3, handler.Requests.Select(r => r.Original).Distinct().Count());
        Assert.Equal(["Bearer sk-rotated-1", "Bearer sk-rotated-2", "Bearer sk-rotated-3"], handler.Requests.Select(r => r.Headers["Authorization"]));
    }

    [Fact]
    public async Task RetriesStopAtTheConfiguredCount()
    {
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.Overloaded()));

        var exception = await Assert.ThrowsAsync<JevHttpException>(() => Create(handler, o => o.AdditionalRetries = 2)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(529, exception.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task StatusesThatWillFailAgainAreNotRetried(HttpStatusCode status)
    {
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(FakeResponses.Json("{}", status)));

        await Assert.ThrowsAsync<JevHttpException>(() => Create(handler, o => o.AdditionalRetries = 3)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AResponseTheSdkCannotAcceptIsNotRetried()
    {
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Always("{\"not\":\"an answer\"}");

        await Assert.ThrowsAsync<JevProtocolException>(() => Create(handler, o => o.AdditionalRetries = 3)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ANetworkFailureIsRetriedOnlyWhenThatIsAskedFor()
    {
        var (plan, _, _) = Plan();
        HttpMessageHandler Flaky() => FakeHttpMessageHandler.Sequence(
            () => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"),
            () => FakeResponses.Json(Answer()));

        await Assert.ThrowsAsync<JevTransportException>(() => Create(Flaky(), o => o.AdditionalRetries = 1)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        var result = await Create(Flaky(), o => { o.AdditionalRetries = 1; o.RetryNetworkFailures = true; })
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Metadata.Attempts);
    }

    /// <summary>A server's requested wait is honoured or declined, never shortened.</summary>
    /// <remarks>
    /// With no deadline, nothing but <see cref="JevClientOptions.MaximumServerRetryWait"/> (30 s by
    /// default) stands between the call and a sixty-second wait, so this is the one test that sees
    /// that check on its own. The clock never moves: a call that decided to wait would never finish,
    /// and <c>WaitAsync</c> gives up on it rather than the test hanging.
    /// </remarks>
    [Fact]
    public async Task AWaitLongerThanAllowedEndsTheCallWithTheWaitAttached()
    {
        var clock = new FakeTimeProvider();
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Sequence(() => FakeResponses.RateLimited(retryAfter: "60"), () => FakeResponses.Json(Answer()));

        var call = Create(handler, o => { o.AdditionalRetries = 3; o.Timeout = Timeout.InfiniteTimeSpan; o.TimeProvider = clock; })
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<JevHttpException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(60), exception.RetryAfter);
        Assert.Single(handler.Requests);
    }

    /// <summary>
    /// A wait longer than the allowance is declined however it is spelled — in milliseconds, or in
    /// more seconds than the header parser holds — and never replaced by the SDK's own backoff,
    /// which in these tests is zero.
    /// </summary>
    [Theory]
    [InlineData(null, "172800000")]
    [InlineData("99999999999", null)]
    public async Task AnOversizedWaitIsDeclinedRatherThanReplacedByTheBackoff(string? retryAfter, string? retryAfterMilliseconds)
    {
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Sequence(() => FakeResponses.RateLimited(retryAfter, retryAfterMilliseconds), () => FakeResponses.Json(Answer()));

        var exception = await Assert.ThrowsAsync<JevHttpException>(() => Create(handler, o => o.AdditionalRetries = 3)
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.True(exception.RetryAfter > TimeSpan.FromDays(1));
        Assert.Single(handler.Requests);
    }

    /// <summary>The same wait, allowed, is waited out in full on the injected clock.</summary>
    [Fact]
    public async Task AWaitWithinTheAllowanceIsHonouredInFull()
    {
        var clock = new FakeTimeProvider();
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Sequence(() => FakeResponses.RateLimited(retryAfter: "60"), () => FakeResponses.Json(Answer()));

        var call = Create(handler, o =>
            {
                o.AdditionalRetries = 1;
                o.Timeout = Timeout.InfiniteTimeSpan;
                o.MaximumServerRetryWait = TimeSpan.FromSeconds(90);
                o.TimeProvider = clock;
            })
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        await WaitUntil(() => handler.Requests.Count == 1);
        clock.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(handler.Requests);

        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await call;

        Assert.Equal(2, result.Metadata.Attempts);
    }

    [Fact]
    public async Task AServerWaitIsSpentOnTheInjectedClockBeforeRetrying()
    {
        var clock = new FakeTimeProvider();
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Sequence(() => FakeResponses.RateLimited(retryAfterMilliseconds: "1500"), () => FakeResponses.Json(Answer()));

        var call = Create(handler, o => { o.AdditionalRetries = 1; o.TimeProvider = clock; })
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        await WaitUntil(() => handler.Requests.Count == 1);
        clock.Advance(TimeSpan.FromMilliseconds(1499));
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Single(handler.Requests);

        clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await call;

        Assert.Equal(2, result.Metadata.Attempts);
    }

    [Fact]
    public async Task ARetryThatCouldNotStartBeforeTheDeadlineIsNotAttempted()
    {
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Sequence(() => FakeResponses.RateLimited(retryAfter: "20"), () => FakeResponses.Json(Answer()));

        var exception = await Assert.ThrowsAsync<JevHttpException>(() => Create(handler, o => { o.AdditionalRetries = 1; o.Timeout = TimeSpan.FromSeconds(10); })
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(429, exception.StatusCode);
        Assert.Single(handler.Requests);
    }

    /// <summary>HttpClient.Timeout does not cover a body read under ResponseHeadersRead; the SDK's deadline does.</summary>
    [Fact]
    public async Task TheDeadlineCoversABodyThatNeverArrives()
    {
        var clock = new FakeTimeProvider();
        var content = new StalledContent();
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));

        var call = Create(handler, o => { o.TimeProvider = clock; o.Timeout = TimeSpan.FromSeconds(30); })
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        await content.Started.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(30));

        var exception = await Assert.ThrowsAsync<JevTimeoutException>(() => call);

        Assert.Equal(TimeSpan.FromSeconds(30), exception.Timeout);
        Assert.Equal(JevRequestTransmission.Started, exception.Transmission);
        Assert.IsAssignableFrom<TimeoutException>(exception);
    }

    [Fact]
    public async Task TheDeadlineIsOneBudgetAcrossEveryAttempt()
    {
        var clock = new FakeTimeProvider();
        var (plan, _, _) = Plan();

        // Each attempt takes 8 s of the 20 s budget before answering 503.
        var handler = new FakeHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8), clock, cancellationToken);
            return FakeResponses.Json("{}", HttpStatusCode.ServiceUnavailable);
        });

        var call = Create(handler, o => { o.TimeProvider = clock; o.Timeout = TimeSpan.FromSeconds(20); o.AdditionalRetries = 5; })
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 6 && !call.IsCompleted; i++)
        {
            await WaitUntil(() => handler.Requests.Count > i || call.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(8));
        }

        await Assert.ThrowsAnyAsync<Exception>(() => call);

        // 8 + 8 = 16 s used; a third 8 s attempt cannot finish inside 20 s, and the budget was not reset per attempt.
        Assert.InRange(handler.Requests.Count, 2, 3);
    }

    [Fact]
    public async Task AResponseThatArrivesAfterTheDeadlineIsNotReturned()
    {
        var clock = new FakeTimeProvider();
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(31));
            return Task.FromResult(FakeResponses.Json(Answer()));
        });

        await Assert.ThrowsAsync<JevTimeoutException>(() => Create(handler, o => { o.TimeProvider = clock; o.Timeout = TimeSpan.FromSeconds(30); })
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The deadline is a time, not only a timer. A timer's callback can run late, and a response
    /// decoded in that window has still arrived after the deadline.
    /// </summary>
    [Fact]
    public async Task AResponseDecodedPastTheDeadlineIsRefusedBeforeTheTimerHasFired()
    {
        var clock = new LateTimerClock();
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromSeconds(31));
            return Task.FromResult(FakeResponses.Json(Answer()));
        });

        var exception = await Assert.ThrowsAsync<JevTimeoutException>(() => Create(handler, o => { o.TimeProvider = clock; o.Timeout = TimeSpan.FromSeconds(30); })
            .EvaluateAsync("text", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.FromSeconds(30), exception.Timeout);
    }

    [Fact]
    public async Task TheCallersCancellationIsReportedAsItsOwn()
    {
        var content = new StalledContent();
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var cancellation = new CancellationTokenSource();

        var call = Create(handler).EvaluateAsync("text", plan, cancellationToken: cancellation.Token);
        await content.Started.WaitAsync(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task AnAlreadyCancelledTokenSendsNothingButStillValidatesArguments()
    {
        var (plan, _, _) = Plan();
        var handler = FakeHttpMessageHandler.Always(Answer());
        var client = Create(handler);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        Assert.Throws<ArgumentNullException>(() => { _ = client.EvaluateAsync("text", null!, cancellationToken: cancellation.Token); });

        var call = client.EvaluateAsync("text", plan, cancellationToken: cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.True(call.IsCanceled);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task WithNoQueueACallThatFindsNoPermitFailsAtOnce()
    {
        var gate = new TaskCompletionSource();
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler(async (_, _) =>
        {
            await gate.Task;
            return FakeResponses.Json(Answer());
        });

        var client = Create(handler, o => o.ConcurrencyLimit = 1);
        var first = client.EvaluateAsync("one", plan, cancellationToken: TestContext.Current.CancellationToken);
        await WaitUntil(() => handler.Requests.Count == 1);

        var refused = await Assert.ThrowsAsync<JevLimitException>(() => client.EvaluateAsync("two", plan, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JevLimit.Queue, refused.Limit);
        Assert.Equal(JevRequestTransmission.NotStarted, refused.Transmission);

        gate.SetResult();
        await first;

        // The permit came back.
        await client.EvaluateAsync("three", plan, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AQueuedCallWaitsForAPermitWithinItsDeadline()
    {
        var gate = new TaskCompletionSource();
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler(async (_, _) =>
        {
            await gate.Task;
            return FakeResponses.Json(Answer());
        });

        var client = Create(handler, o => { o.ConcurrencyLimit = 1; o.MaxQueuedRequests = 1; });
        var first = client.EvaluateAsync("one", plan, cancellationToken: TestContext.Current.CancellationToken);
        await WaitUntil(() => handler.Requests.Count == 1);

        var queued = client.EvaluateAsync("two", plan, cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<JevLimitException>(() => client.EvaluateAsync("three", plan, cancellationToken: TestContext.Current.CancellationToken));

        Assert.False(queued.IsCompleted);
        gate.SetResult();

        await first;
        await queued;
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task WaitingInTheQueueCountsAgainstTheDeadline()
    {
        var clock = new FakeTimeProvider();
        var gate = new TaskCompletionSource();
        var (plan, _, _) = Plan();
        var handler = new FakeHttpMessageHandler(async (_, _) =>
        {
            await gate.Task;
            return FakeResponses.Json(Answer());
        });

        var client = Create(handler, o => { o.ConcurrencyLimit = 1; o.MaxQueuedRequests = 1; o.TimeProvider = clock; o.Timeout = TimeSpan.FromSeconds(5); });
        var first = client.EvaluateAsync("one", plan, new JevRequestOptions { Timeout = Timeout.InfiniteTimeSpan }, TestContext.Current.CancellationToken);
        await WaitUntil(() => handler.Requests.Count == 1);

        var queued = client.EvaluateAsync("two", plan, cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(5));

        var exception = await Assert.ThrowsAsync<JevTimeoutException>(() => queued);
        Assert.Equal(JevRequestTransmission.NotStarted, exception.Transmission);

        gate.SetResult();
        await first;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met within 10 seconds.");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>A clock whose timers never fire: a timer callback that has not run yet, for as long as the test needs.</summary>
    private sealed class LateTimerClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _timestamp, by.Ticks);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new SilentTimer();

        private sealed class SilentTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
