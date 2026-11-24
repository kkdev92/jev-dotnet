using System.Text.Json;
using Kkdev92.Jev.Extensions.AI;
using Kkdev92.Jev.TestSupport;
using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.Tests;

public sealed class ChatAdapterTests
{
    internal static (JevClient Judge, FakeHttpMessageHandler Handler, JevDecisionPlan Plan, JevNoulHandle Handle) Judge()
    {
        var builder = new JevDecisionPlanBuilder();
        var handle = builder.AddNoul("q", "Decide.");
        var handler = FakeHttpMessageHandler.Always(new SystemOneResponseBuilder().Noul("q", 0.9).Build());
        return (new JevClient(new HttpClient(handler), new() { AuthenticationMode = JevAuthenticationMode.None }), handler, builder.Build(), handle);
    }

    [Fact]
    public async Task AllowSendsOnlyTheLastUserTextAndChecksTheWholeReturnedText()
    {
        var (judge, handler, plan, handle) = Judge();
        var policy = new JevGuardPolicy(new(plan, r => r.Get(handle).Probability > 0.5 ? JevGuardDecision.Allow : JevGuardDecision.Review));
        using var inner = new FakeChat();
        using var guard = new JevGuardChatClient(inner, judge, policy, policy, leaveOpen: true);
        ChatMessage[] messages = [new(ChatRole.System, "private-sentinel"), new(ChatRole.User, "previous"), new(ChatRole.User, "last")];
        var reply = await guard.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("answer", reply.Text);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("""{"request":[{"role":"user","text":"last"}]}""", State(handler.Requests[0]));
        Assert.Equal("""{"response":[{"role":"assistant","text":"answer"}]}""", State(handler.Requests[1]));

        inner.Response = new ChatResponse([new(ChatRole.Assistant, "answer"), new(ChatRole.Tool, "uninspected")]);
        var ex = await Assert.ThrowsAsync<JevAdapterException>(() => guard.GetResponseAsync(messages, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JevAdapterFailureReason.UnsupportedContent, ex.ReasonCode);
        Assert.Equal(3, handler.Requests.Count);
    }

    private static string State(CapturedRequest request)
    {
        using var doc = JsonDocument.Parse(request.Body);
        return doc.RootElement.GetProperty("state").GetRawText();
    }

    [Fact]
    public async Task ReviewFromNestedRouterSurvivesOuterGenerationBoundary()
    {
        var (judge, handler, plan, _) = Judge();
        using var leaf = new FakeChat();
        using var router = new JevRoutingChatClient<int>(judge, new(plan, _ => JevRouteDecision<int>.RequireReview()),
            new Dictionary<int, IChatClient> { [1] = leaf });
        using var guard = new JevGuardChatClient(router, judge, new(new(plan, _ => JevGuardDecision.Allow)), leaveOpen: true);
        var ex = await Assert.ThrowsAsync<JevReviewRequiredException>(() => guard.GetResponseAsync([new(ChatRole.User, "text")],
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(JevAdapterStage.Routing, ex.Stage);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(0, leaf.Calls);
    }

    [Fact]
    public async Task ProjectionFailureDropsProviderDetailsAndStopsGeneration()
    {
        var (judge, _, plan, _) = Judge();
        using var inner = new FakeChat();
        using var guard = new JevGuardChatClient(inner, judge, new(new(plan, _ => throw new InvalidOperationException("private-sentinel"))));
        var ex = await Assert.ThrowsAsync<JevProjectionException>(() => guard.GetResponseAsync([new(ChatRole.User, "text")],
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private-sentinel", ex.ToString(), StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
        Assert.Equal(0, inner.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BufferedGuardHoldsAllUpdatesAndReturnsIndependentSnapshots(bool allow)
    {
        var (judge, handler, plan, _) = Judge();
        using var inner = new FakeChat();
        var guardOptions = new JevGuardOptions { OutputStreamingMode = JevOutputStreamingMode.Buffer };
        using var guard = new JevGuardChatClient(inner, judge, outputPolicy: new(new(plan, _ => allow ? JevGuardDecision.Allow : JevGuardDecision.Block)), options: guardOptions);
        var stream = guard.GetStreamingResponseAsync([new(ChatRole.User, "question")], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Empty(handler.Requests);
        await using var enumerator = stream.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        if (allow)
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("first", enumerator.Current.Text);
            Assert.Null(enumerator.Current.RawRepresentation);
            Assert.True(await enumerator.MoveNextAsync());
            Assert.Equal("second", enumerator.Current.Text);
            Assert.False(await enumerator.MoveNextAsync());
        }
        else
        {
            await Assert.ThrowsAsync<JevGuardRejectedException>(() => enumerator.MoveNextAsync().AsTask());
        }
        Assert.Equal("""{"response":[{"role":"assistant","text":"firstsecond"}]}""", State(Assert.Single(handler.Requests)));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task StreamReportsTheOriginalCancellationToken(bool cancelMethod, bool cancelEnumeration)
    {
        var (judge, handler, plan, _) = Judge();
        using var method = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        using var inner = new FakeChat();
        using var guard = new JevGuardChatClient(inner, judge, new(new(plan, _ => JevGuardDecision.Allow)));
        var stream = guard.GetStreamingResponseAsync([new(ChatRole.User, "ticket")], cancellationToken: method.Token);
        var iterator = stream.GetAsyncEnumerator(enumeration.Token);
        Assert.True(await iterator.MoveNextAsync());
        if (cancelMethod) { await method.CancelAsync(); }
        if (cancelEnumeration) { await enumeration.CancelAsync(); }
        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => iterator.MoveNextAsync().AsTask());
        Assert.Equal(cancelMethod ? method.Token : enumeration.Token, ex.CancellationToken);
        Assert.Null(ex.InnerException);
        await iterator.DisposeAsync();
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task EarlyConsumerDisposalReportsOnlyCleanupFailure()
    {
        var (judge, _, plan, _) = Judge();
        using var inner = new FakeChat { FaultOnCancellation = true };
        using var guard = new JevGuardChatClient(inner, judge, new(new(plan, _ => JevGuardDecision.Allow)));
        var iterator = guard.GetStreamingResponseAsync([new(ChatRole.User, "ticket")],
            cancellationToken: TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await iterator.MoveNextAsync());
        var ex = await Assert.ThrowsAsync<JevAdapterException>(() => iterator.DisposeAsync().AsTask());
        Assert.Equal(JevAdapterStage.Lifecycle, ex.Stage);
        Assert.Equal(JevAdapterFailureReason.DownstreamFailure, ex.ReasonCode);
        Assert.DoesNotContain("private-sentinel", ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LifecycleSanitizesUnrelatedCancellationAndDisposesTheInnerOnlyOnce()
    {
        var (judge, _, plan, _) = Judge();
        var inner = new FakeChat { FaultOnLifecycle = true };
        var guard = new JevGuardChatClient(inner, judge, new(new(plan, _ => JevGuardDecision.Allow)));
        var service = Assert.Throws<JevAdapterException>(() => guard.GetService(typeof(string)));
        var dispose = Assert.Throws<JevAdapterException>(guard.Dispose);
        guard.Dispose();
        Assert.Equal(1, inner.DisposeCalls);
        foreach (var ex in new[] { service, dispose })
        {
            Assert.Equal(JevAdapterStage.Lifecycle, ex.Stage);
            Assert.Equal(JevAdapterFailureReason.DownstreamFailure, ex.ReasonCode);
            Assert.Null(ex.InnerException);
            Assert.DoesNotContain("private-sentinel", ex.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(5, 1)]
    public async Task NestedGuardsHonorTheShorterDeadline(int outerSeconds, int innerSeconds)
    {
        var (judge, handler, plan, _) = Judge();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var leaf = new FakeChat();
        using var inner = new JevGuardChatClient(leaf, judge, new(new(plan, _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            return JevGuardDecision.Allow;
        })), options: new() { TotalTimeout = TimeSpan.FromSeconds(innerSeconds), TimeProvider = clock }, leaveOpen: true);
        using var outer = new JevGuardChatClient(inner, judge, new(new(plan, _ => JevGuardDecision.Allow)),
            options: new() { TotalTimeout = TimeSpan.FromSeconds(outerSeconds), TimeProvider = clock }, leaveOpen: true);
        var ex = await Assert.ThrowsAsync<JevPipelineTimeoutException>(() => outer.GetResponseAsync([new(ChatRole.User, "ticket")],
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(TimeSpan.FromSeconds(1), ex.Timeout);
        Assert.Equal(0, leaf.Calls);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FunctionValidatesExactArgumentNameUnicodeAndBothByteLimits()
    {
        var (judge, handler, plan, _) = Judge();
        var function = JevAIFunction.Create(judge, new JevProjection<string>(plan, _ => "abcd"), ChatTestJson.Default.String,
            JsonElement.Parse("""{"type":"string"}"""), "check", "check", new() { MaxInputUtf8Bytes = 3, MaxOutputUtf8Bytes = 5 });
        foreach (var (key, value) in new[] { ("State", "a"), ("state", "    "), ("state", "ああ"), ("state", new string((char)0xd800, 1)) })
        {
            var input = new AIFunctionArguments { [key] = value };
            var ex = await Assert.ThrowsAsync<JevAdapterException>(() => function.InvokeAsync(input, TestContext.Current.CancellationToken).AsTask());
            Assert.Equal(JevAdapterFailureReason.InvalidFunctionArguments, ex.ReasonCode);
        }
        Assert.Empty(handler.Requests);
        var tooLarge = await Assert.ThrowsAsync<JevAdapterException>(() => function.InvokeAsync(new() { ["state"] = "あ" },
            TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(JevAdapterFailureReason.ResultLimitExceeded, tooLarge.ReasonCode);
        Assert.Single(handler.Requests);
    }

    internal sealed class FakeChat : IChatClient
    {
        public bool FaultOnCancellation { get; init; }
        public bool FaultOnLifecycle { get; init; }
        public int DisposeCalls { get; private set; }
        public int Calls { get; private set; }
        public ChatResponse Response { get; set; } = new(new ChatMessage(ChatRole.Assistant, "answer"));
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Response);
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (FaultOnCancellation)
            {
                cancellationToken.Register(static () => throw new InvalidOperationException("private-sentinel"));
            }
            await Task.CompletedTask;
            var update = new ChatResponseUpdate(ChatRole.Assistant, "first") { RawRepresentation = new object() };
            yield return update;
            update.Contents.Clear();
            update.Contents.Add(new TextContent("second"));
            yield return update;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => FaultOnLifecycle ? throw new OperationCanceledException("private-sentinel") : null;
        public void Dispose()
        {
            DisposeCalls++;
            if (FaultOnLifecycle) { throw new OperationCanceledException("private-sentinel"); }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class ChatTestJson : System.Text.Json.Serialization.JsonSerializerContext;
