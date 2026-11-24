using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace Kkdev92.Jev.ContractTests;

public sealed class CancellationPrivacyTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task CancellationJoinsFaultingNotificationAndReleasesAdmission(bool callerWins, bool models)
    {
        var testToken = TestContext.Current.CancellationToken;
        using var caller = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeTimeProvider();
        var credential = new WaitingCredential(entered, release);
        var handler = FakeHttpMessageHandler.Always(FakeResponses.Models());
        var client = Clients.Create(handler, o =>
        {
            o.Credential = credential;
            o.TimeProvider = clock;
            o.ConcurrencyLimit = 1;
        });

        Task call = models ? client.GetModelsAsync(cancellationToken: caller.Token)
            : client.EvaluateAsync("text", Clients.Plan().Plan, cancellationToken: caller.Token);
        if (callerWins)
        {
            caller.Cancel();
        }
        else
        {
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), testToken);
            Assert.False(call.IsCompleted);
            await Assert.ThrowsAsync<JevLimitException>(() => client.GetModelsAsync(cancellationToken: testToken));
        }
        finally
        {
            release.Set();
        }

        if (callerWins)
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => call);
            Assert.Equal(caller.Token, error.CancellationToken);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain("private-sentinel", error.ToString(), StringComparison.Ordinal);
        }
        else
        {
            var error = await Assert.ThrowsAsync<JevTimeoutException>(() => call);
            Assert.Null(error.InnerException);
            Assert.Equal(JevRequestTransmission.NotStarted, error.Transmission);
        }

        Assert.Empty(handler.Requests);
        await client.GetModelsAsync(new JevRequestOptions { AuthenticationMode = JevAuthenticationMode.None }, testToken);
        Assert.Single(handler.Requests);
    }

    private sealed class WaitingCredential(TaskCompletionSource entered, ManualResetEventSlim release) : IJevCredential
    {
        public async ValueTask<string> GetApiKeyAsync(CancellationToken cancellationToken)
        {
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() =>
            {
                stopped.TrySetResult();
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
                throw new InvalidOperationException("private-sentinel");
            });
            await stopped.Task;
            throw new OperationCanceledException("private-sentinel", cancellationToken);
        }
    }
}
