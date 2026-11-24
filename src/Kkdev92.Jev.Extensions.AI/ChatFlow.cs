using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.Extensions.AI;

internal sealed record ChatTarget(IReadOnlyList<ChatMessage> Messages, IChatClient? Client, string? Blocked);

internal sealed class ChatFlow(PipelineSettings settings, Action checkLifetime,
    Func<IEnumerable<ChatMessage>, ChatOptions?, Pipeline, Task<ChatTarget>> prepare,
    Func<IReadOnlyList<ChatMessage>, ChatResponse, Pipeline, Task<string?>>? inspect = null, JevGuardOptions? guard = null)
{
    private static ChatResponse Blocked(string text) =>
        new(new ChatMessage(ChatRole.Assistant, text)) { FinishReason = ChatFinishReason.ContentFilter };

    public async Task<ChatResponse> ResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken caller)
    {
        var run = new Pipeline(settings, caller);
        bool failed = true;
        try
        {
            run.Check();
            var target = await prepare(messages, options, run).ConfigureAwait(false);
            ChatResponse response;
            if (target.Blocked is { } inputBlock)
            {
                response = Blocked(inputBlock);
            }
            else
            {
                response = await run.GenerateAsync(target.Client!, target.Messages, options).ConfigureAwait(false);
                if (inspect is not null && await inspect(target.Messages, response, run).ConfigureAwait(false) is { } outputBlock)
                {
                    response = Blocked(outputBlock);
                }
            }
            failed = false;
            return response;
        }
        finally
        {
            await run.FinishAsync(failed).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> Stream(IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken method,
        [EnumeratorCancellation] CancellationToken enumeration = default)
    {
        var run = new Pipeline(settings, method, enumeration);
        bool failed = true;
        bool delivering = false;
        try
        {
            checkLifetime();
            run.Check();
            if (inspect is not null && guard!.OutputStreamingMode != JevOutputStreamingMode.Buffer)
            {
                throw new NotSupportedException("Output guarding requires buffered streaming.");
            }
            var target = await prepare(messages, options, run).ConfigureAwait(false);
            run.Check();
            if (target.Blocked is { } inputBlock)
            {
                delivering = true;
                yield return BlockUpdate(inputBlock);
                delivering = false;
            }
            else
            {
                run.Stage = JevAdapterStage.Generation;
                var buffer = inspect is null ? null : new ChatBuffer(guard!);
                var sequence = Open(target, options, run);
                bool exhausted = false;
                try
                {
                    while (await MoveAsync(sequence, run).ConfigureAwait(false))
                    {
                        ChatResponseUpdate? update;
                        try { update = sequence.Current; }
                        catch (Exception ex) when (Failures.Recoverable(ex)) { throw Downstream(ex, run); }
                        run.Check();
                        if (buffer is null)
                        {
                            if (update is null) { throw new JevAdapterException(run.Stage, JevAdapterFailureReason.DownstreamFailure); }
                            if (update.ContinuationToken is not null) { throw new JevAdapterException(run.Stage, JevAdapterFailureReason.UnsupportedContent); }
                            delivering = true;
                            yield return update;
                            delivering = false;
                        }
                        else
                        {
                            buffer.Add(update);
                        }
                    }
                    exhausted = true;
                }
                finally
                {
                    if (!exhausted) { run.Stop(); }
                    try { await sequence.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) when (Failures.Recoverable(ex))
                    {
                        if (exhausted || delivering)
                        {
                            delivering = false;
                            throw Failures.From(ex, JevAdapterStage.Lifecycle, JevAdapterFailureReason.DownstreamFailure);
                        }
                    }
                }

                if (buffer is not null)
                {
                    run.Check();
                    var response = buffer.Response();
                    var blocked = await inspect!(target.Messages, response, run).ConfigureAwait(false);
                    run.Check();
                    if (blocked is not null)
                    {
                        delivering = true;
                        yield return BlockUpdate(blocked);
                        delivering = false;
                    }
                    else
                    {
                        foreach (var update in buffer.Updates)
                        {
                            run.Check();
                            delivering = true;
                            yield return update;
                            delivering = false;
                        }
                    }
                }
            }
            failed = false;
        }
        finally
        {
            if (failed) { run.Stop(); }
            await run.FinishAsync(failed && !delivering).ConfigureAwait(false);
        }
    }

    private static ChatResponseUpdate BlockUpdate(string text) =>
        new(ChatRole.Assistant, text) { FinishReason = ChatFinishReason.ContentFilter };

    private static Exception Downstream(Exception ex, Pipeline run)
    {
        run.Check();
        return Failures.From(ex, JevAdapterStage.Generation, JevAdapterFailureReason.DownstreamFailure);
    }

    private static IAsyncEnumerator<ChatResponseUpdate> Open(ChatTarget target, ChatOptions? options, Pipeline run)
    {
        try
        {
            var stream = target.Client!.GetStreamingResponseAsync(target.Messages, options, run.Token);
            return stream.GetAsyncEnumerator(run.Token) ?? throw new InvalidOperationException();
        }
        catch (Exception ex) when (Failures.Recoverable(ex)) { throw Downstream(ex, run); }
    }

    private static async ValueTask<bool> MoveAsync(IAsyncEnumerator<ChatResponseUpdate> enumerator, Pipeline run)
    {
        run.Check();
        try
        {
            var more = await enumerator.MoveNextAsync().ConfigureAwait(false);
            run.Check();
            return more;
        }
        catch (Exception ex) when (Failures.Recoverable(ex)) { throw Downstream(ex, run); }
    }
}
