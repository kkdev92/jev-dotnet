using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Kkdev92.Jev.Extensions.AI.Evaluation;

/// <summary>Handling of eligible operational failures from the judge.</summary>
public enum JevEvaluationFailureBehavior
{
    /// <summary>Propagate the failure.</summary>
    Throw = 0,
    /// <summary>Return a failed metric with a null value for each definition.</summary>
    ReturnFailedMetrics = 1,
}

/// <summary>Metadata explicitly copied into successful metrics.</summary>
[Flags]
public enum JevEvaluationMetadata
{
    /// <summary>Omit metadata.</summary>
    None = 0,
    /// <summary>Include judge duration, attempts and token usage.</summary>
    Operational = 1,
    /// <summary>Include model names and request ID if available.</summary>
    RequestIdentifiers = 2,
}

/// <summary>Options captured by an evaluator at construction.</summary>
public sealed class JevEvaluatorOptions
{
    /// <summary>Selection of request and response text.</summary>
    public JevChatStateOptions? StateOptions { get; set; }
    /// <summary>Options for the judge call.</summary>
    public JevRequestOptions? RequestOptions { get; set; }
    /// <summary>Deadline including extraction and metric mapping.</summary>
    public TimeSpan? TotalTimeout { get; set; }
    /// <summary>Clock for the deadline.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
    /// <summary>Outcome when the judge is unavailable.</summary>
    public JevEvaluationFailureBehavior FailureBehavior { get; set; } = JevEvaluationFailureBehavior.Throw;
    /// <summary>Additional diagnostic fields for successful metrics.</summary>
    public JevEvaluationMetadata Metadata { get; set; }
    /// <summary>Maximum number of additional contexts.</summary>
    public int MaxAdditionalContexts { get; set; } = 64;
    /// <summary>Optionally include explicitly chosen evaluation context in judge input.</summary>
    public Func<JevContent, IReadOnlyList<EvaluationContext>, JevContent>? ContextProjector { get; set; }
}

/// <summary>Project one complete Jev result into multiple independently named metrics.</summary>
public sealed class JevEvaluator<T> : IEvaluator
{
    private readonly JevClient _judge;
    private readonly JevProjection<T> _projection;
    private readonly JevMetricDefinition<T>[] _metrics;
    private readonly ChatState _state;
    private readonly JevRequestOptions _request;
    private readonly PipelineSettings _settings;
    private readonly JevEvaluationMetadata _metadata;
    private readonly JevEvaluationFailureBehavior _failure;
    private readonly int _maxContexts;
    private readonly Func<JevContent, IReadOnlyList<EvaluationContext>, JevContent>? _context;

    /// <summary>Capture metric definitions and options. The judge remains caller-owned.</summary>
    public JevEvaluator(JevClient judge, JevProjection<T> projection, IEnumerable<JevMetricDefinition<T>> metrics, JevEvaluatorOptions? options = null)
    {
        _judge = judge ?? throw new ArgumentNullException(nameof(judge));
        _projection = projection ?? throw new ArgumentNullException(nameof(projection));
        ArgumentNullException.ThrowIfNull(metrics);
        _metrics = metrics.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (_metrics.Length == 0 || _metrics.Any(m => m is null || !names.Add(m.Name)))
        {
            throw new ArgumentException("Metric definitions must be nonempty, named and unique.", nameof(metrics));
        }
        EvaluationMetricNames = Array.AsReadOnly(_metrics.Select(m => m.Name).ToArray());
        var o = options ?? new();
        if (!Enum.IsDefined(o.FailureBehavior) || (o.Metadata & ~(JevEvaluationMetadata.Operational | JevEvaluationMetadata.RequestIdentifiers)) != 0)
        {
            throw new ArgumentException("Unsupported evaluation options.", nameof(options));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxAdditionalContexts, 1);
        _settings = new(o.TimeProvider, o.TotalTimeout);
        _state = new(ChatState.Copy(o.StateOptions), JevChatStateScope.RequestAndResponseText);
        _request = Pipeline.Snapshot(o.RequestOptions);
        (_failure, _metadata, _maxContexts, _context) = (o.FailureBehavior, o.Metadata, o.MaxAdditionalContexts, o.ContextProjector);
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<string> EvaluationMetricNames { get; }

    /// <inheritdoc/>
    public async ValueTask<EvaluationResult> EvaluateAsync(IEnumerable<ChatMessage> messages, ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null, IEnumerable<EvaluationContext>? additionalContext = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelResponse);
        var run = new Pipeline(_settings, cancellationToken);
        bool failed = true;
        try
        {
            run.Check();
            if (modelResponse.ContinuationToken is not null)
            {
                throw new JevAdapterException(JevAdapterStage.Evaluation, JevAdapterFailureReason.UnsupportedContent);
            }
            var state = _state.Extract(_state.Materialize(messages, run), modelResponse, run);
            if (additionalContext is not null)
            {
                try
                {
                    var list = new List<EvaluationContext>();
                    foreach (var context in additionalContext)
                    {
                        run.Check();
                        if (_context is null || list.Count >= _maxContexts || context is null)
                        {
                            throw new JevAdapterException(JevAdapterStage.Evaluation, JevAdapterFailureReason.UnsupportedEvaluationContext);
                        }
                        list.Add(context);
                    }
                    if (list.Count > 0) { state = _state.Checked(_context!(state, list.AsReadOnly())); }
                }
                catch (Exception ex) when (Failures.Recoverable(ex))
                {
                    throw Failures.From(ex, JevAdapterStage.StateProjection, JevAdapterFailureReason.InvalidProjection, projection: true);
                }
            }

            JevResult result;
            try
            {
                result = await run.JudgeAsync(_judge, state, _projection.Plan, _request, JevAdapterStage.Evaluation).ConfigureAwait(false);
            }
            catch (Exception ex) when (_failure == JevEvaluationFailureBehavior.ReturnFailedMetrics && Failures.SafeJudge(ex)
                && (ex is not JevLimitException limit || limit.Limit is JevLimit.Queue or JevLimit.ResponseBody))
            {
                run.Check();
                var unavailable = new EvaluationResult(_metrics.Select(m => m.Unavailable()));
                failed = false;
                return unavailable;
            }
            var value = _projection.Project(result);
            var mapped = new List<EvaluationMetric>(_metrics.Length);
            foreach (var definition in _metrics)
            {
                run.Check();
                EvaluationMetric metric;
                try { metric = definition.Create(value); }
                catch (Exception ex) when (Failures.Recoverable(ex))
                {
                    throw Failures.From(ex, JevAdapterStage.ResultProjection, JevAdapterFailureReason.InvalidProjection, projection: true);
                }
                if (_metadata != JevEvaluationMetadata.None) { metric.Metadata = Metadata(result); }
                mapped.Add(metric);
            }
            failed = false;
            return new EvaluationResult(mapped);
        }
        finally { await run.FinishAsync(failed).ConfigureAwait(false); }
    }

    private Dictionary<string, string> Metadata(JevResult result)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var culture = CultureInfo.InvariantCulture;
        if ((_metadata & JevEvaluationMetadata.Operational) != 0)
        {
            values.Add("jev.attempts", result.Metadata.Attempts.ToString(culture));
            values.Add("jev.evaluation_duration_ms", result.Metadata.Duration.TotalMilliseconds.ToString(culture));
            values.Add("jev.input_tokens", result.Usage.InputTokens.ToString(culture));
            values.Add("jev.output_tokens", result.Usage.OutputTokens.ToString(culture));
        }
        if ((_metadata & JevEvaluationMetadata.RequestIdentifiers) != 0)
        {
            values.Add("jev.requested_model", result.RequestedModel);
            values.Add("jev.actual_model", result.ActualModel);
            if (result.Metadata.RequestId is { } id) { values.Add("jev.request_id", id); }
        }
        return values;
    }
}
