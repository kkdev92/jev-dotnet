using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.Extensions.AI;

internal readonly record struct PipelineSettings
{
    public PipelineSettings(TimeProvider clock, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (timeout is { } duration && (duration <= TimeSpan.Zero || duration > TimeSpan.FromMilliseconds(uint.MaxValue - 1)))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The pipeline timeout must be a representable positive duration.");
        }
        Clock = clock;
        Timeout = timeout;
    }

    public TimeProvider Clock { get; }
    public TimeSpan? Timeout { get; }
}

internal sealed class Pipeline
{
    private readonly PipelineSettings _settings;
    private readonly long _start;
    private readonly CancellationToken _caller;
    private readonly CancellationToken _enumeration;
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _first;
    private readonly CancellationTokenRegistration _second;
    private readonly ITimer? _timer;
    private Task? _notice;
    private int _stopping;
    private int _deadline;

    public Pipeline(PipelineSettings settings, CancellationToken caller, CancellationToken enumeration = default)
    {
        _settings = settings;
        _caller = caller;
        _enumeration = enumeration;
        _start = settings.Clock.GetTimestamp();
        _first = caller.UnsafeRegister(static s => ((Pipeline)s!).Stop(), this);
        _second = enumeration.UnsafeRegister(static s => ((Pipeline)s!).Stop(), this);
        if (settings.Timeout is { } duration)
        {
            _timer = settings.Clock.CreateTimer(static s =>
            {
                var pipeline = (Pipeline)s!;
                Volatile.Write(ref pipeline._deadline, 1);
                pipeline.Stop();
            }, this, duration, Timeout.InfiniteTimeSpan);
        }
    }

    public CancellationToken Token => _source.Token;
    public JevAdapterStage Stage { get; set; } = JevAdapterStage.StateProjection;

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopping, 1) == 0)
        {
            _notice = _source.CancelAsync();
        }
    }

    public void Check()
    {
        if (_caller.IsCancellationRequested || _enumeration.IsCancellationRequested)
        {
            throw new OperationCanceledException("The Jev pipeline was canceled.", null,
                _caller.IsCancellationRequested ? _caller : _enumeration);
        }
        if (_settings.Timeout is { } budget && (Volatile.Read(ref _deadline) != 0 || _settings.Clock.GetElapsedTime(_start) >= budget))
        {
            throw new JevPipelineTimeoutException(Stage, budget);
        }
    }

    public async ValueTask FinishAsync(bool primaryFailure)
    {
        if (primaryFailure) { Stop(); }
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
        }

        await _first.DisposeAsync().ConfigureAwait(false);
        await _second.DisposeAsync().ConfigureAwait(false);
        if (_notice is { } notice)
        {
            await notice.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _source.Dispose();
        Check();
        if (!primaryFailure && _notice?.IsFaulted == true)
        {
            throw new JevAdapterException(JevAdapterStage.Lifecycle, JevAdapterFailureReason.DownstreamFailure);
        }
    }

    public static JevRequestOptions Snapshot(JevRequestOptions? o)
    {
        if (o?.CaptureErrorBody == true || o?.CaptureRawResponse == true)
        {
            throw new ArgumentException("Capture is unavailable for adapter calls.", nameof(o));
        }
        if (o?.AuthenticationMode is { } mode && !Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(o), "Unknown authentication mode.");
        }
        return new JevRequestOptions
        {
            Model = o?.Model,
            UseLongContext = o?.UseLongContext,
            AuthenticationMode = o?.AuthenticationMode,
            Credential = o?.Credential,
            Timeout = o?.Timeout,
            AdditionalRetries = o?.AdditionalRetries,
            CaptureRawResponse = false,
            CaptureErrorBody = false,
        };
    }

    public async Task<JevResult> JudgeAsync(JevClient judge, JevContent state, JevDecisionPlan plan, JevRequestOptions options, JevAdapterStage stage)
    {
        Stage = stage;
        Check();
        try
        {
            var result = await judge.EvaluateContentAsync(state, plan, options, Token).ConfigureAwait(false);
            Check();
            return result;
        }
        catch (Exception ex) when (Failures.Recoverable(ex))
        {
            Check();
            if (Failures.SafeJudge(ex))
            {
                throw;
            }
            throw Failures.From(ex, stage, JevAdapterFailureReason.EvaluationUnavailable);
        }
    }

    public static void Preflight(ChatOptions? options, bool router)
    {
        if (options?.ContinuationToken is not null || options?.AllowBackgroundResponses == true || (router && options?.ConversationId is not null))
        {
            throw new NotSupportedException("The requested stateful operation is unavailable on this adapter.");
        }
    }

    public async Task<ChatResponse> GenerateAsync(IChatClient client, IReadOnlyList<ChatMessage> messages, ChatOptions? options)
    {
        Stage = JevAdapterStage.Generation;
        Check();
        try
        {
            var response = await client.GetResponseAsync(messages, options, Token).ConfigureAwait(false);
            Check();
            if (response is null)
            {
                throw new JevAdapterException(Stage, JevAdapterFailureReason.DownstreamFailure);
            }
            if (response.ContinuationToken is not null)
            {
                throw new JevAdapterException(Stage, JevAdapterFailureReason.UnsupportedContent);
            }
            return response;
        }
        catch (Exception ex) when (Failures.Recoverable(ex))
        {
            Check();
            throw Failures.From(ex, Stage, JevAdapterFailureReason.DownstreamFailure);
        }
    }
}
