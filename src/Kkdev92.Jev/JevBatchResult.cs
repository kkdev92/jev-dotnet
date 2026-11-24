namespace Kkdev92.Jev;

/// <summary>Immutable input-ordered snapshot of a completed or interrupted batch.</summary>
public sealed class JevBatchResult
{
    internal JevBatchResult(JevBatchItemResult[] items, TimeSpan duration)
    {
        Items = Array.AsReadOnly(items);
        Duration = duration;
        foreach (var item in items)
        {
            switch (item.Status)
            {
                case JevBatchItemStatus.NotStarted: NotStartedCount++; break;
                case JevBatchItemStatus.Succeeded: SucceededCount++; break;
                case JevBatchItemStatus.Failed: FailedCount++; break;
                case JevBatchItemStatus.Canceled: CanceledCount++; break;
            }

            if (item.Transmission > Transmission)
            {
                Transmission = item.Transmission;
            }
        }
    }

    internal JevRequestTransmission Transmission { get; }
    /// <summary>One entry per accepted input, in input order.</summary>
    public IReadOnlyList<JevBatchItemResult> Items { get; }
    /// <summary>Number of fully validated results.</summary>
    public int SucceededCount { get; }
    /// <summary>Number of failed evaluations.</summary>
    public int FailedCount { get; }
    /// <summary>Number of started evaluations that stopped cooperatively.</summary>
    public int CanceledCount { get; }
    /// <summary>Number of inputs whose evaluation never began.</summary>
    public int NotStartedCount { get; }
    /// <summary>Elapsed time through worker cleanup.</summary>
    public TimeSpan Duration { get; }
}

/// <summary>Outcome for one input.</summary>
public sealed class JevBatchItemResult
{
    internal JevBatchItemResult(int index, JevBatchItemStatus status, JevRequestTransmission transmission, JevResult? result = null, Exception? error = null)
    {
        Index = index;
        Status = status;
        Transmission = transmission;
        Result = result;
        Error = error;
    }

    /// <summary>Zero-based input index.</summary>
    public int Index { get; }
    /// <summary>Outcome of the evaluation.</summary>
    public JevBatchItemStatus Status { get; }
    /// <summary>Fully validated response on success.</summary>
    public JevResult? Result { get; }
    /// <summary>The underlying failure, available by explicit access.</summary>
    public Exception? Error { get; }
    /// <summary>Transmission across all attempts, including retries.</summary>
    public JevRequestTransmission Transmission { get; }
}

/// <summary>Final item status.</summary>
public enum JevBatchItemStatus
{
    /// <summary>Never entered evaluation.</summary>
    NotStarted = 0,
    /// <summary>The complete response passed validation.</summary>
    Succeeded = 1,
    /// <summary>The evaluation raised an error.</summary>
    Failed = 2,
    /// <summary>An evaluation in progress was stopped.</summary>
    Canceled = 3,
}

/// <summary>A batch stopped after an item failed.</summary>
public sealed class JevBatchException : JevException
{
    internal JevBatchException(JevBatchResult snapshot, int failedIndex)
        : base("An item in the batch failed.", JevOperation.EvaluateMany, snapshot.Transmission)
        => (Snapshot, FailedIndex) = (snapshot, failedIndex);

    /// <summary>Final results, including completed and unstarted inputs.</summary>
    public JevBatchResult Snapshot { get; }
    /// <summary>The first failure that caused this batch to stop.</summary>
    public int FailedIndex { get; }
}

/// <summary>The caller canceled the batch.</summary>
public sealed class JevBatchCanceledException : OperationCanceledException
{
    internal JevBatchCanceledException(JevBatchResult snapshot, CancellationToken caller)
        : base("The batch was canceled.", null, caller) => Snapshot = snapshot;

    /// <summary>The snapshot after all active workers finished.</summary>
    public JevBatchResult Snapshot { get; }
}

/// <summary>The batch exhausted its overall time budget.</summary>
public sealed class JevBatchTimeoutException : TimeoutException
{
    internal JevBatchTimeoutException(JevBatchResult snapshot, TimeSpan timeout)
        : base("The batch deadline expired.") => (Snapshot, Timeout) = (snapshot, timeout);

    /// <summary>The snapshot after all active workers finished.</summary>
    public JevBatchResult Snapshot { get; }
    /// <summary>The configured batch deadline.</summary>
    public TimeSpan Timeout { get; }
}
