using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.Extensions.AI;

/// <summary>Associates one guard decision with a frozen state projection and request configuration.</summary>
public sealed class JevGuardPolicy
{
    /// <summary>Create a policy for use on input, output, or both.</summary>
    public JevGuardPolicy(JevProjection<JevGuardDecision> projection, JevChatStateOptions? stateOptions = null, JevRequestOptions? requestOptions = null)
    {
        Projection = projection ?? throw new ArgumentNullException(nameof(projection));
        State = ChatState.Copy(stateOptions);
        // Placement resolves Unspecified. Validate the common options immediately.
        var output = State.Scope is JevChatStateScope.ResponseText or JevChatStateScope.RequestAndResponseText;
        _ = new ChatState(State, output ? JevChatStateScope.ResponseText : JevChatStateScope.LatestUserText);
        Request = Pipeline.Snapshot(requestOptions);
    }
    /// <summary>The typed decision over the validated judge result.</summary>
    public JevProjection<JevGuardDecision> Projection { get; }
    internal JevChatStateOptions State { get; }
    internal JevRequestOptions Request { get; }
}

/// <summary>Apply an input or output guard around one chat client.</summary>
public sealed class JevGuardChatClient : DelegatingChatClient
{
    private readonly ChatFlow _flow;
    private readonly bool _leaveOpen;
    private int _disposed;

    /// <summary>Create a guarded client. The judge is borrowed. By default this wrapper owns the inner client.</summary>
    public JevGuardChatClient(IChatClient innerClient, JevClient judge, JevGuardPolicy? inputPolicy = null,
        JevGuardPolicy? outputPolicy = null, JevGuardOptions? options = null, bool leaveOpen = false) : base(innerClient)
    {
        ArgumentNullException.ThrowIfNull(judge);
        if (inputPolicy is null && outputPolicy is null) { throw new ArgumentException("At least one guard policy is required."); }
        _leaveOpen = leaveOpen;
        var guard = Snapshot(options);
        var input = inputPolicy is null ? null : new ChatState(inputPolicy.State, JevChatStateScope.LatestUserText);
        var output = outputPolicy is null ? null : new ChatState(outputPolicy.State, JevChatStateScope.ResponseText, true);
        _flow = new(new PipelineSettings(guard.TimeProvider, guard.TotalTimeout), () => ObjectDisposedException.ThrowIf(_disposed != 0, this), PrepareAsync,
            output is null ? null : InspectAsync, guard);

        async Task<ChatTarget> PrepareAsync(IEnumerable<ChatMessage> messages, ChatOptions? chat, Pipeline run)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            Pipeline.Preflight(chat, false);
            var list = (input ?? output!).Materialize(messages, run, output);
            var blocked = input is null ? null : await DecideAsync(inputPolicy!, input.Extract(list, null, run), run, JevAdapterStage.InputGuard).ConfigureAwait(false);
            return new(list, innerClient, blocked);
        }

        Task<string?> InspectAsync(IReadOnlyList<ChatMessage> list, ChatResponse response, Pipeline run)
            => DecideAsync(outputPolicy!, output!.Extract(list, response, run), run, JevAdapterStage.OutputGuard);

        async Task<string?> DecideAsync(JevGuardPolicy policy, JevContent state, Pipeline run, JevAdapterStage stage)
        {
            var result = await run.JudgeAsync(judge, state, policy.Projection.Plan, policy.Request, stage).ConfigureAwait(false);
            var decision = policy.Projection.Project(result);
            run.Check();
            return decision switch
            {
                JevGuardDecision.Allow => null,
                JevGuardDecision.Review => throw new JevReviewRequiredException(stage),
                JevGuardDecision.Block when guard.BlockBehavior == JevBlockBehavior.ReturnMessage => guard.BlockedMessage,
                JevGuardDecision.Block => throw new JevGuardRejectedException(stage),
                _ => throw new JevProjectionException(JevAdapterStage.ResultProjection, JevAdapterFailureReason.InvalidDecision),
            };
        }
    }

    internal static JevGuardOptions Snapshot(JevGuardOptions? options)
    {
        var o = options ?? new();
        if (!Enum.IsDefined(o.BlockBehavior) || !Enum.IsDefined(o.OutputStreamingMode)
            || (o.BlockBehavior == JevBlockBehavior.ReturnMessage && string.IsNullOrWhiteSpace(o.BlockedMessage)))
        {
            throw new ArgumentException("Invalid guard behavior or blocked message.", nameof(options));
        }
        _ = new PipelineSettings(o.TimeProvider, o.TotalTimeout);
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxBufferedContentItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxBufferedUpdates, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxBufferedUtf8Bytes, 1);
        return new()
        {
            TotalTimeout = o.TotalTimeout,
            TimeProvider = o.TimeProvider,
            BlockBehavior = o.BlockBehavior,
            BlockedMessage = o.BlockedMessage,
            OutputStreamingMode = o.OutputStreamingMode,
            MaxBufferedUtf8Bytes = o.MaxBufferedUtf8Bytes,
            MaxBufferedUpdates = o.MaxBufferedUpdates,
            MaxBufferedContentItems = o.MaxBufferedContentItems
        };
    }

    /// <inheritdoc/>
    public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _flow.ResponseAsync(messages, options, cancellationToken);
    }

    /// <inheritdoc/>
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _flow.Stream(messages, options, cancellationToken);

    /// <inheritdoc/>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(serviceType);
        try { return base.GetService(serviceType, serviceKey); }
        catch (Exception ex) when (Failures.Recoverable(ex))
        {
            throw Failures.From(ex, JevAdapterStage.Lifecycle, JevAdapterFailureReason.DownstreamFailure);
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0 && !_leaveOpen)
        {
            try { base.Dispose(true); }
            catch (Exception ex) when (Failures.Recoverable(ex))
            {
                throw Failures.From(ex, JevAdapterStage.Lifecycle, JevAdapterFailureReason.DownstreamFailure);
            }
        }
    }
}

/// <summary>Register a single guard wrapper in a Microsoft.Extensions.AI pipeline.</summary>
public static class JevChatClientBuilderExtensions
{
    /// <summary>Use a borrowed judge for input and output decisions.</summary>
    public static ChatClientBuilder UseJevGuard(this ChatClientBuilder builder, JevClient judge, JevGuardPolicy? inputPolicy = null,
        JevGuardPolicy? outputPolicy = null, JevGuardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(judge);
        return builder.UseJevGuard(_ => judge, inputPolicy, outputPolicy, options);
    }

    /// <summary>Resolve the judge once per pipeline build.</summary>
    public static ChatClientBuilder UseJevGuard(this ChatClientBuilder builder, Func<IServiceProvider, JevClient> judgeFactory,
        JevGuardPolicy? inputPolicy = null, JevGuardPolicy? outputPolicy = null, JevGuardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(judgeFactory);
        if (inputPolicy is null && outputPolicy is null) { throw new ArgumentException("At least one guard policy is required."); }
        var guard = JevGuardChatClient.Snapshot(options);
        return builder.Use((inner, provider) =>
        {
            JevClient? judge;
            try { judge = judgeFactory(provider); }
            catch (Exception ex) when (Failures.Recoverable(ex)) { throw new ArgumentException("The judge factory failed.", nameof(judgeFactory)); }
            return new JevGuardChatClient(inner, judge ?? throw new ArgumentException("The judge factory returned null.", nameof(judgeFactory)), inputPolicy, outputPolicy, guard);
        });
    }
}
