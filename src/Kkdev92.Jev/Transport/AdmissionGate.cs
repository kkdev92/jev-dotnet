namespace Kkdev92.Jev.Transport;

/// <summary>
/// Bounds how many calls run at once, with a bounded queue in front.
/// </summary>
/// <remarks>
/// <para>
/// Created only when <see cref="JevClientOptions.ConcurrencyLimit"/> is set: by default there is no
/// gate and no hidden queue. A call holds its permit for its whole duration, backoff included, so a
/// burst of retrying calls cannot pile up behind the limit unnoticed.
/// </para>
/// <para>
/// When every permit is taken, a call waits only if a queue slot is free, and fails at once with
/// <see cref="JevLimitException"/> otherwise. The wait counts against the call's deadline and is
/// cancellable. Waiters are not guaranteed to be admitted in arrival order.
/// </para>
/// </remarks>
internal sealed class AdmissionGate(int limit, int maxQueued)
{
    private readonly SemaphoreSlim _permits = new(limit, limit);
    private int _queued;

    public int Limit { get; } = limit;

    public int MaxQueued { get; } = maxQueued;

    /// <summary>Takes a permit, waiting in the queue if there is room.</summary>
    /// <returns>True when the call had to wait.</returns>
    /// <exception cref="JevLimitException">No permit and no queue slot.</exception>
    public async ValueTask<bool> EnterAsync(JevOperation operation, CancellationToken cancellationToken)
    {
        if (await _permits.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return false;
        }

        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            Interlocked.Decrement(ref _queued);
            throw new JevLimitException(operation, JevLimit.Queue, MaxQueued, JevRequestTransmission.NotStarted);
        }

        try
        {
            await _permits.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }
    }

    public void Exit() => _permits.Release();
}
