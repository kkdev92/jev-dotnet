using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.Extensions.AI;

/// <summary>Snapshots supported fields before advancing a provider that may reuse its update object.</summary>
internal sealed class ChatBuffer(JevGuardOptions limits)
{
    private readonly List<ChatResponseUpdate> _updates = [];
    private int _bytes = "{\"updates\":[]}"u8.Length;
    private int _contents;
    public IReadOnlyList<ChatResponseUpdate> Updates => _updates;

    private static JevAdapterException Limit() => new(JevAdapterStage.BufferedStreaming, JevAdapterFailureReason.BufferLimitExceeded);
    private static JevAdapterException Unsupported() => new(JevAdapterStage.BufferedStreaming, JevAdapterFailureReason.UnsupportedStreamingContent);

    public void Add(ChatResponseUpdate? input)
    {
        if (input is null || input.ContinuationToken is not null
            || (input.Role is { } role && string.IsNullOrWhiteSpace(role.Value)))
        {
            throw Unsupported();
        }
        if (_updates.Count >= limits.MaxBufferedUpdates || input.Contents.Count > limits.MaxBufferedContentItems - _contents)
        {
            throw Limit();
        }

        var remaining = limits.MaxBufferedUtf8Bytes - _bytes - (_updates.Count > 0 ? 1 : 0);
        if (remaining < 1) { throw Limit(); }
        var bytes = new JsonBudget(remaining, JevAdapterStage.BufferedStreaming, JevAdapterFailureReason.BufferLimitExceeded);
        using var writer = new Utf8JsonWriter(bytes, JsonBudget.Format);
        writer.WriteStartObject();
        void Scalar(string name, string? value)
        {
            if (value is not null)
            {
                if (value.Length > remaining) { throw Limit(); }
                if (!JsonBudget.Valid(value)) { throw Unsupported(); }
            }
            writer.WriteString(name, value);
        }
        Scalar("role", input.Role?.Value);
        Scalar("responseId", input.ResponseId);
        Scalar("messageId", input.MessageId);
        Scalar("conversationId", input.ConversationId);
        Scalar("modelId", input.ModelId);
        Scalar("authorName", input.AuthorName);
        if (input.CreatedAt is { } time) { writer.WriteString("createdAt", time); } else { writer.WriteNull("createdAt"); }
        Scalar("finishReason", input.FinishReason?.Value);
        writer.WriteStartArray("contents");
        var copy = new ChatResponseUpdate
        {
            Role = input.Role,
            ResponseId = input.ResponseId,
            MessageId = input.MessageId,
            ConversationId = input.ConversationId,
            ModelId = input.ModelId,
            AuthorName = input.AuthorName,
            CreatedAt = input.CreatedAt,
            FinishReason = input.FinishReason,
        };
        foreach (var item in input.Contents)
        {
            writer.WriteStartObject();
            switch (item)
            {
                case TextContent t:
                    writer.WriteString("type", "text");
                    Scalar("text", t.Text);
                    copy.Contents.Add(new TextContent(t.Text));
                    break;
                case UsageContent u:
                    writer.WriteString("type", "usage");
                    var d = u.Details;
                    Number(writer, "inputTokenCount", d.InputTokenCount);
                    Number(writer, "outputTokenCount", d.OutputTokenCount);
                    Number(writer, "totalTokenCount", d.TotalTokenCount);
                    Number(writer, "cachedInputTokenCount", d.CachedInputTokenCount);
                    Number(writer, "reasoningTokenCount", d.ReasoningTokenCount);
                    copy.Contents.Add(new UsageContent(new UsageDetails
                    {
                        InputTokenCount = d.InputTokenCount,
                        OutputTokenCount = d.OutputTokenCount,
                        TotalTokenCount = d.TotalTokenCount,
                        CachedInputTokenCount = d.CachedInputTokenCount,
                        ReasoningTokenCount = d.ReasoningTokenCount,
                    }));
                    break;
                default: throw Unsupported();
            }
            writer.WriteEndObject();
            writer.Flush();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        _bytes = limits.MaxBufferedUtf8Bytes - remaining + bytes.Written.Length;
        _contents += copy.Contents.Count;
        _updates.Add(copy);
    }

    private static void Number(Utf8JsonWriter writer, string key, long? value)
    {
        if (value is { } n) { writer.WriteNumber(key, n); } else { writer.WriteNull(key); }
    }

    public ChatResponse Response()
    {
        if (_bytes > limits.MaxBufferedUtf8Bytes) { throw Limit(); }
        var response = new ChatResponse([]);
        ChatMessage? message = null;
        foreach (var update in _updates)
        {
            if (message is null || Different(message.MessageId, update.MessageId) || Different(message.AuthorName, update.AuthorName)
                || (update.Role is { } role && role != message.Role))
            {
                message = new ChatMessage(update.Role ?? ChatRole.Assistant, []);
                response.Messages.Add(message);
            }
            if (!string.IsNullOrEmpty(update.ResponseId)) { response.ResponseId = update.ResponseId; }
            response.ConversationId = update.ConversationId ?? response.ConversationId;
            response.ModelId = update.ModelId ?? response.ModelId;
            response.FinishReason = update.FinishReason ?? response.FinishReason;
            if (update.CreatedAt > DateTimeOffset.UnixEpoch)
            {
                response.CreatedAt ??= update.CreatedAt;
                message.CreatedAt ??= update.CreatedAt;
            }
            if (!string.IsNullOrEmpty(update.MessageId)) { message.MessageId = update.MessageId; }
            message.AuthorName = update.AuthorName ?? message.AuthorName;
            foreach (var item in update.Contents)
            {
                if (item is TextContent text) { message.Contents.Add(new TextContent(text.Text)); }
                else if (item is UsageContent usage)
                {
                    var total = response.Usage ??= new UsageDetails();
                    var part = usage.Details;
                    total.InputTokenCount = AddCount(total.InputTokenCount, part.InputTokenCount);
                    total.OutputTokenCount = AddCount(total.OutputTokenCount, part.OutputTokenCount);
                    total.TotalTokenCount = AddCount(total.TotalTokenCount, part.TotalTokenCount);
                    total.CachedInputTokenCount = AddCount(total.CachedInputTokenCount, part.CachedInputTokenCount);
                    total.ReasoningTokenCount = AddCount(total.ReasoningTokenCount, part.ReasoningTokenCount);
                }
            }
        }
        return response;
    }

    private static bool Different(string? previous, string? current) =>
        !string.IsNullOrEmpty(previous) && !string.IsNullOrEmpty(current) && previous != current;

    private static long? AddCount(long? current, long? next)
    {
        if (next is null) { return current; }
        try { return checked(current.GetValueOrDefault() + next.Value); }
        catch (OverflowException) { throw Unsupported(); }
    }
}
