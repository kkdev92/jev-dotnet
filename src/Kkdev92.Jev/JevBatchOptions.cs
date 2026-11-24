namespace Kkdev92.Jev;

/// <summary>Limits for one group of evaluations sharing a plan.</summary>
public sealed class JevBatchOptions
{
    /// <summary>Maximum workers in this batch. The client admission limit also applies.</summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>Maximum accepted input count.</summary>
    public int MaxItems { get; set; } = 1024;

    /// <summary>One deadline for input preparation, scheduling and evaluation.</summary>
    public TimeSpan OverallTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>How ordinary SDK failures affect subsequent items.</summary>
    public JevBatchFailureMode FailureMode { get; set; } = JevBatchFailureMode.FailFast;
}

/// <summary>Controls how a batch treats item failures.</summary>
public enum JevBatchFailureMode
{
    /// <summary>Stop scheduling when an item fails.</summary>
    FailFast = 0,
    /// <summary>Keep ordinary SDK failures in the result and continue.</summary>
    Collect = 1,
}
