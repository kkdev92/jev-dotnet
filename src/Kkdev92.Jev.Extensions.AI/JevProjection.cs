namespace Kkdev92.Jev.Extensions.AI;

/// <summary>Maps a complete result from one immutable plan to an application decision.</summary>
public sealed class JevProjection<T>
{
    private readonly Func<JevResult, T> _project;

    /// <summary>Associate a result selector with its plan.</summary>
    public JevProjection(JevDecisionPlan plan, Func<JevResult, T> project)
    {
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _project = project ?? throw new ArgumentNullException(nameof(project));
    }

    /// <summary>The plan accepted by this projection.</summary>
    public JevDecisionPlan Plan { get; }

    /// <summary>Read the validated result through the application selector.</summary>
    public T Project(JevResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!ReferenceEquals(result.Plan, Plan))
        {
            throw new ArgumentException("The result belongs to another plan.", nameof(result));
        }

        try
        {
            return _project(result);
        }
        catch (Exception ex) when (Failures.Recoverable(ex))
        {
            throw Failures.From(ex, JevAdapterStage.ResultProjection, JevAdapterFailureReason.InvalidProjection, projection: true);
        }
    }
}

/// <summary>A classified failure containing only finite diagnostic fields.</summary>
public class JevAdapterException : Exception
{
    internal JevAdapterException(JevAdapterStage stage, JevAdapterFailureReason reason) : base($"Jev {stage} failed ({reason}).")
        => (Stage, ReasonCode) = (stage, reason);
    /// <summary>Operation in which the failure occurred.</summary>
    public JevAdapterStage Stage { get; }
    /// <summary>Diagnostic reason.</summary>
    public JevAdapterFailureReason ReasonCode { get; }
}

/// <summary>A host projection failed to produce a usable value.</summary>
public sealed class JevProjectionException : JevAdapterException
{
    internal JevProjectionException(JevAdapterStage stage, JevAdapterFailureReason reason) : base(stage, reason) { }
}

/// <summary>A guard declined the request or response.</summary>
public sealed class JevGuardRejectedException : JevAdapterException
{
    internal JevGuardRejectedException(JevAdapterStage stage) : base(stage, JevAdapterFailureReason.GuardBlocked) { }
}

/// <summary>The policy requires a review.</summary>
public sealed class JevReviewRequiredException : JevAdapterException
{
    internal JevReviewRequiredException(JevAdapterStage stage) : base(stage, JevAdapterFailureReason.ReviewRequired) { }
}

/// <summary>The adapter's end-to-end time budget expired.</summary>
public sealed class JevPipelineTimeoutException : TimeoutException
{
    internal JevPipelineTimeoutException(JevAdapterStage stage, TimeSpan timeout) : base("The Jev pipeline deadline expired.")
        => (Stage, Timeout) = (stage, timeout);
    /// <summary>Stage active at the deadline.</summary>
    public JevAdapterStage Stage { get; }
    /// <summary>The deadline classification.</summary>
    public JevAdapterFailureReason ReasonCode => JevAdapterFailureReason.DeadlineExceeded;
    /// <summary>The configured duration.</summary>
    public TimeSpan Timeout { get; }
}

internal static class Failures
{
    public static bool Recoverable(Exception ex) => ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    public static Exception From(Exception ex, JevAdapterStage stage, JevAdapterFailureReason reason, bool projection = false) => ex switch
    {
        JevProjectionException p => new JevProjectionException(p.Stage, p.ReasonCode),
        JevGuardRejectedException g => new JevGuardRejectedException(g.Stage),
        JevReviewRequiredException r => new JevReviewRequiredException(r.Stage),
        JevPipelineTimeoutException t => new JevPipelineTimeoutException(t.Stage, t.Timeout),
        JevAdapterException a when a.GetType() == typeof(JevAdapterException) => new JevAdapterException(a.Stage, a.ReasonCode),
        OperationCanceledException when stage != JevAdapterStage.Lifecycle => projection ? new JevProjectionException(stage, JevAdapterFailureReason.UnexpectedCancellation)
            : new JevAdapterException(stage, JevAdapterFailureReason.UnexpectedCancellation),
        _ => projection ? new JevProjectionException(stage, reason) : new JevAdapterException(stage, reason),
    };

    public static bool SafeJudge(Exception ex)
    {
        if (ex.InnerException is not null || ex.Data.Count != 0)
        {
            return false;
        }
        return ex switch
        {
            JevHttpException h => h.Operation == JevOperation.Evaluate && h.ErrorBody is null,
            JevProtocolException or JevTransportException or JevLimitException => ((JevException)ex).Operation == JevOperation.Evaluate,
            JevTimeoutException t => t.Operation == JevOperation.Evaluate,
            _ => false,
        };
    }
}
