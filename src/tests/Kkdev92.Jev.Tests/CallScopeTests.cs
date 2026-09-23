using Kkdev92.Jev.Internal;
using Kkdev92.Jev.Transport;
using Microsoft.Extensions.Time.Testing;

namespace Kkdev92.Jev.Tests;

/// <summary>The last check a result passes before it is returned.</summary>
public sealed class CallScopeTests
{
    private static CallScope Scope(FakeTimeProvider clock, CancellationToken callerToken)
    {
        var settings = ClientSettings.From(new JevClientOptions
        {
            Credential = new StaticJevCredential("sk-test"),
            TimeProvider = clock,
            Timeout = TimeSpan.FromSeconds(30),
        });

        return new CallScope(settings, gate: null, JevOperation.Evaluate, CallSettings.Resolve(settings, options: null), callerToken);
    }

    [Fact]
    public void BeforeTheDeadlineAResultIsLetThrough()
    {
        var clock = new FakeTimeProvider();
        using var scope = Scope(clock, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(29));

        scope.ThrowIfDeadlinePassed();
    }

    [Fact]
    public void PastTheDeadlineAResultIsRefusedAsATimeout()
    {
        var clock = new FakeTimeProvider();
        using var scope = Scope(clock, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Throws<JevTimeoutException>(scope.ThrowIfDeadlinePassed);
    }

    /// <summary>
    /// Both at once is still past the deadline, so nothing is returned; what is reported is the
    /// caller's cancellation, in the same order every other failure of the call is classified.
    /// </summary>
    [Fact]
    public void PastTheDeadlineTheCallersCancellationIsReportedFirst()
    {
        var clock = new FakeTimeProvider();
        using var caller = new CancellationTokenSource();
        using var scope = Scope(clock, caller.Token);

        clock.Advance(TimeSpan.FromSeconds(31));
        caller.Cancel();

        var exception = Assert.Throws<OperationCanceledException>(scope.ThrowIfDeadlinePassed);
        Assert.Equal(caller.Token, exception.CancellationToken);
    }
}
