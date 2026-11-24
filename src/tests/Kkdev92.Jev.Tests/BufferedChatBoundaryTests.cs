using System.Runtime.CompilerServices;
using System.Text.Json;
using Kkdev92.Jev.Extensions.AI;
using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.Tests;

public sealed class BufferedChatBoundaryTests
{
    [Theory]
    [InlineData("updates", JevAdapterFailureReason.BufferLimitExceeded)]
    [InlineData("contents", JevAdapterFailureReason.BufferLimitExceeded)]
    [InlineData("bytes", JevAdapterFailureReason.BufferLimitExceeded)]
    [InlineData("unknown", JevAdapterFailureReason.UnsupportedStreamingContent)]
    [InlineData("unicode", JevAdapterFailureReason.UnsupportedStreamingContent)]
    [InlineData("overflow", JevAdapterFailureReason.UnsupportedStreamingContent)]
    [InlineData("usage-only", JevAdapterFailureReason.NoAnalyzableText)]
    [InlineData("tool-role", JevAdapterFailureReason.UnsupportedContent)]
    public async Task BufferedFailuresReleaseNothingAndKeepThePrimaryCause(string variant, JevAdapterFailureReason reason)
    {
        var (judge, handler, plan, _) = ChatAdapterTests.Judge();
        var options = new JevGuardOptions { OutputStreamingMode = JevOutputStreamingMode.Buffer };
        ChatResponseUpdate[] updates = [new(ChatRole.Assistant, "first"), new(null, "second")];
        switch (variant)
        {
            case "updates": options.MaxBufferedUpdates = 1; break;
            case "contents": options.MaxBufferedContentItems = 1; break;
            case "bytes": options.MaxBufferedUtf8Bytes = 16; break;
            case "unknown": updates[1].Contents.Add(new DataContent(new byte[] { 1 }, "application/octet-stream")); break;
            case "unicode": updates[1].Contents.Add(new TextContent(new string((char)0xd800, 1))); break;
            case "overflow":
                updates[0].Contents.Add(new UsageContent(new UsageDetails { InputTokenCount = long.MaxValue }));
                updates[1].Contents.Add(new UsageContent(new UsageDetails { InputTokenCount = 1 }));
                break;
            case "usage-only": updates = [new() { Contents = [new UsageContent(new UsageDetails { InputTokenCount = 2 })] }]; break;
            case "tool-role": updates = [new(ChatRole.Tool, ""), new(null, "sensitive")]; break;
        }
        bool disposeFault = variant is "updates" or "contents" or "bytes" or "unknown" or "unicode";
        using var provider = new StreamingProvider(updates, disposeFault);
        using var guard = new JevGuardChatClient(provider, judge, outputPolicy: new(new(plan, _ => JevGuardDecision.Allow)), options: options, leaveOpen: true);
        var iterator = guard.GetStreamingResponseAsync([new(ChatRole.User, "ticket")], cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<JevAdapterException>(() => iterator.MoveNextAsync().AsTask());
        Assert.Equal(reason, ex.ReasonCode);
        Assert.Null(ex.InnerException);
        Assert.DoesNotContain("private-sentinel", ex.ToString(), StringComparison.Ordinal);
        await iterator.DisposeAsync();
        Assert.Equal(1, provider.Disposals);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RepeatedNonconsecutiveMessageIdsKeepBoundariesAndUsageStaysOutsideJudgment()
    {
        var (judge, handler, plan, _) = ChatAdapterTests.Judge();
        ChatResponseUpdate[] updates = [
            new(null, "a") { MessageId = "one" },
            new(null, "b"),
            new(ChatRole.Assistant, "c") { MessageId = "two" },
            new(null, "d") { MessageId = "one" },
        ];
        updates[1].Contents.Add(new UsageContent(new UsageDetails { InputTokenCount = 21 }));
        using var provider = new StreamingProvider(updates, false);
        using var guard = new JevGuardChatClient(provider, judge, outputPolicy: new(new(plan, _ => JevGuardDecision.Allow)),
            options: new() { OutputStreamingMode = JevOutputStreamingMode.Buffer }, leaveOpen: true);
        var received = new List<ChatResponseUpdate>();
        await foreach (var update in guard.GetStreamingResponseAsync([new(ChatRole.User, "input")], cancellationToken: TestContext.Current.CancellationToken))
        {
            received.Add(update);
        }
        Assert.Equal(["a", "b", "c", "d"], received.Select(u => u.Text));
        Assert.Equal(21, Assert.IsType<UsageContent>(received[1].Contents[1]).Details.InputTokenCount);
        using var state = JsonDocument.Parse(Assert.Single(handler.Requests).Body);
        Assert.Equal("""{"response":[{"role":"assistant","text":"ab"},{"role":"assistant","text":"c"},{"role":"assistant","text":"d"}]}""",
            state.RootElement.GetProperty("state").GetRawText());
        Assert.Equal(1, provider.Disposals);
    }

    private sealed class StreamingProvider(ChatResponseUpdate[] updates, bool faultOnDispose) : IChatClient
    {
        public int Disposals { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Yield();
                foreach (var update in updates)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return update;
                }
            }
            finally
            {
                Disposals++;
                if (faultOnDispose) { throw new InvalidOperationException("private-sentinel"); }
            }
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
