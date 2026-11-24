using Kkdev92.Jev.Internal;
using Kkdev92.Jev.Requests;
using Kkdev92.Jev.Transport;

namespace Kkdev92.Jev;

public sealed partial class JevClient
{
    /// <summary>Evaluate a fixed list of texts against one plan, returning outcomes in input order.</summary>
    public Task<JevBatchResult> EvaluateManyAsync(IReadOnlyList<string> states, JevDecisionPlan plan,
        JevBatchOptions? batchOptions = null, JevRequestOptions? options = null, CancellationToken cancellationToken = default)
        => StartBatch(states, JevContent.FromText, plan, batchOptions, options, cancellationToken);

    /// <summary>Evaluate text or structured content against one plan. Inputs are copied before the first call.</summary>
    public Task<JevBatchResult> EvaluateManyContentAsync(IReadOnlyList<JevContent> states, JevDecisionPlan plan,
        JevBatchOptions? batchOptions = null, JevRequestOptions? options = null, CancellationToken cancellationToken = default)
        => StartBatch(states, static state => state, plan, batchOptions, options, cancellationToken);

    private Task<JevBatchResult> StartBatch<T>(IReadOnlyList<T> states, Func<T, JevContent> convert,
        JevDecisionPlan plan, JevBatchOptions? options, JevRequestOptions? request, CancellationToken caller)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(plan);
        var limit = options?.MaxItems ?? 1024;
        var concurrency = options?.MaxConcurrency ?? 4;
        var timeout = options?.OverallTimeout ?? Timeout.InfiniteTimeSpan;
        var mode = options?.FailureMode ?? JevBatchFailureMode.FailFast;
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(concurrency, 1);
        ClientSettings.ValidateTimeout(timeout, nameof(JevBatchOptions.OverallTimeout));
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Undefined batch failure mode.");
        }

        var call = ResolveCall(request);
        var count = states.Count;
        if (count > limit || count < 0)
        {
            throw new ArgumentException("The input count is outside the batch limits.", nameof(states));
        }

        var batch = new Batch(this, plan, call, timeout, mode, caller, count);
        return batch.RunAsync(states, convert, Math.Min(count, Math.Min(concurrency, _gate?.Limit ?? concurrency)));
    }

    private sealed class Batch(JevClient client, JevDecisionPlan plan, CallSettings call, TimeSpan timeout,
        JevBatchFailureMode mode, CancellationToken caller, int count)
    {
        private enum Stop { None, Caller, Deadline, Failed }

        private readonly long _started = client._settings.TimeProvider.GetTimestamp();
        private readonly JevContent[] _inputs = new JevContent[count];
        private readonly JevBatchItemResult[] _items = new JevBatchItemResult[count];
        private readonly CancellationTokenSource _workers = new();
        private readonly Lock _sync = new();
        private Stop _reason;
        private int _failedIndex;
        private int _next;
        private Task? _notification;

        private TimeSpan Elapsed => client._settings.TimeProvider.GetElapsedTime(_started);

        public async Task<JevBatchResult> RunAsync<T>(IReadOnlyList<T> states, Func<T, JevContent> convert, int workerCount)
        {
            using var source = _workers;
            var registration = caller.UnsafeRegister(static state => ((Batch)state!).Observe(Stop.Caller), this);
            ITimer? timer = null;
            try
            {
                if (timeout != Timeout.InfiniteTimeSpan)
                {
                    var remaining = timeout - Elapsed;
                    timer = client._settings.TimeProvider.CreateTimer(static state => ((Batch)state!).Observe(Stop.Deadline),
                        this, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
                }

                for (var i = 0; i < _inputs.Length && Observe(Stop.None); i++)
                {
                    var content = convert(states[i]);
                    ThrowIfNotSendable(content);
                    CheckImages(null, plan, content);
                    _inputs[i] = content;
                }

                if (Observe(Stop.None))
                {
                    var workers = new Task[workerCount];
                    for (var i = 0; i < workers.Length; i++)
                    {
                        workers[i] = WorkAsync();
                    }

                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
            }
            finally
            {
                if (timer is not null)
                {
                    await timer.DisposeAsync().ConfigureAwait(false);
                }

                await registration.DisposeAsync().ConfigureAwait(false);
                if (_notification is { } notification)
                {
                    await notification.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }

            for (var i = 0; i < _items.Length; i++)
            {
                _items[i] ??= new JevBatchItemResult(i, JevBatchItemStatus.NotStarted, JevRequestTransmission.NotStarted);
            }

            var snapshot = new JevBatchResult(_items, Elapsed);
            if (caller.IsCancellationRequested)
            {
                throw new JevBatchCanceledException(snapshot, caller);
            }

            if (_reason == Stop.Failed)
            {
                throw new JevBatchException(snapshot, _failedIndex);
            }

            if (_reason == Stop.Deadline || (timeout != Timeout.InfiniteTimeSpan && Elapsed >= timeout))
            {
                throw new JevBatchTimeoutException(snapshot, timeout);
            }

            return snapshot;
        }

        private async Task WorkAsync()
        {
            while (Transition(Stop.None, -1, takeIndex: true, out var index) && index >= 0 && Observe(Stop.None))
            {
                var history = new AttemptHistory();
                try
                {
                    var result = await client.EvaluateCoreAsync(StateSource.FromContent(_inputs[index]), plan,
                        [], call, _workers.Token, history).ConfigureAwait(false);
                    _items[index] = new(index, JevBatchItemStatus.Succeeded, history.Transmission, result);
                }
                catch (OperationCanceledException) when (_workers.IsCancellationRequested)
                {
                    _items[index] = new(index, JevBatchItemStatus.Canceled, history.Transmission);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    _items[index] = new(index, JevBatchItemStatus.Failed, history.Transmission, error: ex);
                    if (mode == JevBatchFailureMode.FailFast || ex is not (JevException or JevTimeoutException))
                    {
                        Observe(Stop.Failed, index);
                    }
                }
            }
        }

        private bool Observe(Stop cause, int failedIndex = -1) => Transition(cause, failedIndex, false, out _);

        private bool Transition(Stop cause, int failedIndex, bool takeIndex, out int index)
        {
            index = -1;
            lock (_sync)
            {
                if (_reason != Stop.None)
                {
                    return false;
                }

                if (caller.IsCancellationRequested)
                {
                    cause = Stop.Caller;
                }
                else if (timeout != Timeout.InfiniteTimeSpan && Elapsed >= timeout)
                {
                    cause = Stop.Deadline;
                }

                if (cause == Stop.None)
                {
                    if (takeIndex && _next < _items.Length)
                    {
                        index = _next++;
                    }

                    return true;
                }

                _reason = cause;
                _failedIndex = failedIndex;
            }

            // The winner is a worker, an upstream registration, or RunAsync itself. All three
            // are joined before cleanup reads the task. Never invoke token callbacks under _sync.
            _notification = _workers.CancelAsync();
            return false;
        }
    }
}
