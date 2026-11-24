using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.Extensions.AI;

/// <summary>Select one route, an explicitly configured fallback, or review.</summary>
public readonly struct JevRouteDecision<TRoute> where TRoute : notnull
{
    private readonly TRoute? _route;
    private JevRouteDecision(JevRouteKind kind, TRoute? route) => (Kind, _route) = (kind, route);
    /// <summary>The route decision.</summary>
    public JevRouteKind Kind { get; }
    /// <summary>The chosen key when Kind is Route.</summary>
    public TRoute Route => Kind == JevRouteKind.Route ? _route! : throw new InvalidOperationException("This decision has no route.");
    /// <summary>Choose an explicitly registered route.</summary>
    public static JevRouteDecision<TRoute> UseRoute(TRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return new(JevRouteKind.Route, route);
    }
    /// <summary>Choose the configured fallback.</summary>
    public static JevRouteDecision<TRoute> UseFallback() => new(JevRouteKind.Fallback, default);
    /// <summary>Stop for review.</summary>
    public static JevRouteDecision<TRoute> RequireReview() => new(JevRouteKind.Review, default);
}

/// <summary>Routes a stateless chat request to one borrowed client after a typed judge decision.</summary>
public sealed class JevRoutingChatClient<TRoute> : IChatClient where TRoute : notnull
{
    private readonly ChatFlow _flow;
    private int _disposed;

    /// <summary>Copy the route map and freeze routing settings.</summary>
    public JevRoutingChatClient(JevClient judge, JevProjection<JevRouteDecision<TRoute>> projection,
        IReadOnlyDictionary<TRoute, IChatClient> routes, IChatClient? fallbackClient = null, JevRoutingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(routes);
        var map = new Dictionary<TRoute, IChatClient>(EqualityComparer<TRoute>.Default);
        foreach (var entry in routes)
        {
            if (entry.Key is null || entry.Value is null || !map.TryAdd(entry.Key, entry.Value))
            {
                throw new ArgumentException("Invalid route mapping.", nameof(routes));
            }
        }
        if (map.Count == 0) { throw new ArgumentException("At least one route is required.", nameof(routes)); }
        var state = new ChatState(ChatState.Copy(options?.StateOptions), JevChatStateScope.LatestUserText);
        var request = Pipeline.Snapshot(options?.RequestOptions);
        var o = options ?? new();
        _flow = new(new PipelineSettings(o.TimeProvider, o.TotalTimeout), () => ObjectDisposedException.ThrowIf(_disposed != 0, this), PrepareAsync);

        async Task<ChatTarget> PrepareAsync(IEnumerable<ChatMessage> messages, ChatOptions? chat, Pipeline run)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            Pipeline.Preflight(chat, true);
            var list = state.Materialize(messages, run);
            var result = await run.JudgeAsync(judge, state.Extract(list, null, run), projection.Plan, request, JevAdapterStage.Routing).ConfigureAwait(false);
            var decision = projection.Project(result);
            run.Check();
            var client = decision.Kind switch
            {
                JevRouteKind.Route => map.TryGetValue(decision.Route, out var route) ? route : throw new JevAdapterException(run.Stage, JevAdapterFailureReason.UnknownRoute),
                JevRouteKind.Fallback => fallbackClient ?? throw new JevAdapterException(run.Stage, JevAdapterFailureReason.MissingFallback),
                JevRouteKind.Review => throw new JevReviewRequiredException(run.Stage),
                _ => throw new JevProjectionException(JevAdapterStage.ResultProjection, JevAdapterFailureReason.InvalidDecision),
            };
            return new(list, client, null);
        }
    }

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        return _flow.ResponseAsync(messages, options, cancellationToken);
    }
    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => _flow.Stream(messages, options, cancellationToken);
    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }
    /// <inheritdoc/>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}
