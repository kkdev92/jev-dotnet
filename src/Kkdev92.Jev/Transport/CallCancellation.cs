namespace Kkdev92.Jev.Transport;

/// <summary>Owns the token handed downstream and joins its notification before releasing it.</summary>
internal sealed class CallCancellation : IAsyncDisposable
{
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _registration;
    private readonly ITimer? _timer;
    private Task? _notification;
    private int _notifying;

    public CallCancellation(TimeSpan timeout, TimeProvider clock, CancellationToken caller)
    {
        Token = _source.Token;
        _registration = caller.UnsafeRegister(static state => ((CallCancellation)state!).Notify(), this);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            _timer = clock.CreateTimer(static state => ((CallCancellation)state!).Notify(), this, timeout, Timeout.InfiniteTimeSpan);
        }
    }

    public CancellationToken Token { get; }

    private void Notify()
    {
        if (Interlocked.Exchange(ref _notifying, 1) == 0)
        {
            _notification = _source.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Join both places that can enter Notify. Even when cleanup races CancelAsync itself,
        // the winning callback has published its task before either registration can be reclaimed.
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        await _registration.DisposeAsync().ConfigureAwait(false);
        if (_notification is { } notification)
        {
            await notification.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _source.Dispose();
    }
}
