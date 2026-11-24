using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.Extensions.AI;

namespace Kkdev92.Jev.Extensions.AI;

internal sealed class JsonBudget(int limit, JevAdapterStage stage, JevAdapterFailureReason reason) : IBufferWriter<byte>
{
    private readonly ArrayBufferWriter<byte> _bytes = new();
    internal static readonly JsonWriterOptions Format = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All), MaxDepth = 64 };
    public ReadOnlyMemory<byte> Written => _bytes.WrittenMemory;
    public Memory<byte> GetMemory(int sizeHint = 0) => _bytes.GetMemory(sizeHint);
    public Span<byte> GetSpan(int sizeHint = 0) => _bytes.GetSpan(sizeHint);
    public void Advance(int count)
    {
        if (count > limit - _bytes.WrittenCount)
        {
            throw new JevAdapterException(stage, reason);
        }
        _bytes.Advance(count);
    }

    public static bool Valid(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsSurrogate(value[i]) && !(char.IsHighSurrogate(value[i]) && ++i < value.Length && char.IsLowSurrogate(value[i])))
            {
                return false;
            }
        }
        return true;
    }
}

internal sealed class ChatState
{
    private readonly JevChatStateScope _scope;
    private readonly HashSet<ChatRole> _roles;
    private readonly JevUnsupportedContentBehavior _behavior;
    private readonly Func<IReadOnlyList<ChatMessage>, ChatResponse?, JevContent>? _projector;
    private readonly bool _guardOutput;
    private readonly int _bytes;
    public int Messages { get; }
    public int Contents { get; }

    internal static JevChatStateOptions Copy(JevChatStateOptions? options) => options is null ? new() : new()
    {
        Scope = options.Scope,
        IncludedRoles = options.IncludedRoles?.ToArray(),
        UnsupportedContentBehavior = options.UnsupportedContentBehavior,
        MaxStateUtf8Bytes = options.MaxStateUtf8Bytes,
        MaxMessages = options.MaxMessages,
        MaxContentItems = options.MaxContentItems,
        Projector = options.Projector,
    };

    public ChatState(JevChatStateOptions o, JevChatStateScope defaultScope, bool guardOutput = false)
    {
        _scope = o.Scope == JevChatStateScope.Unspecified ? defaultScope : o.Scope;
        bool input = defaultScope == JevChatStateScope.LatestUserText;
        if (!Enum.IsDefined(_scope) || !Enum.IsDefined(o.UnsupportedContentBehavior)
            || (_scope != JevChatStateScope.Custom && input != (_scope is JevChatStateScope.LatestUserText or JevChatStateScope.ConversationText))
            || ((_scope == JevChatStateScope.Custom) != (o.Projector is not null))
            || (o.IncludedRoles is not null && _scope != JevChatStateScope.ConversationText))
        {
            throw new ArgumentException("Invalid chat state options.", nameof(o));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxContentItems, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(o.MaxStateUtf8Bytes, 1);
        _roles = [];
        var roles = o.IncludedRoles ?? [ChatRole.User, ChatRole.Assistant];
        foreach (var role in roles)
        {
            if (string.IsNullOrWhiteSpace(role.Value) || !JsonBudget.Valid(role.Value) || !_roles.Add(role))
            {
                throw new ArgumentException("Invalid or duplicate chat role.", nameof(o));
            }
        }
        if (_roles.Count == 0)
        {
            throw new ArgumentException("At least one role is required.", nameof(o));
        }
        (_projector, _behavior, Messages, Contents, _bytes, _guardOutput) =
            (o.Projector, o.UnsupportedContentBehavior, o.MaxMessages, o.MaxContentItems, o.MaxStateUtf8Bytes, guardOutput);
    }

    private static JevAdapterException Error(JevAdapterFailureReason reason) => new(JevAdapterStage.StateProjection, reason);

    public IReadOnlyList<ChatMessage> Materialize(IEnumerable<ChatMessage> messages, Pipeline run, ChatState? secondPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var list = new List<ChatMessage>();
        long contents = 0;
        try
        {
            foreach (var message in messages)
            {
                run.Check();
                Validate(message, ref contents, list.Count + 1);
                if (secondPolicy is not null && (list.Count >= secondPolicy.Messages || contents > secondPolicy.Contents))
                {
                    throw Error(JevAdapterFailureReason.StateLimitExceeded);
                }
                list.Add(message);
            }
        }
        catch (Exception ex) when (Failures.Recoverable(ex))
        {
            run.Check();
            throw Failures.From(ex, JevAdapterStage.StateProjection, JevAdapterFailureReason.InvalidProjection, projection: true);
        }
        return list.AsReadOnly();
    }

    private void Validate(ChatMessage message, ref long contents, long count)
    {
        if (message is null || string.IsNullOrWhiteSpace(message.Role.Value) || !JsonBudget.Valid(message.Role.Value))
        {
            throw Error(JevAdapterFailureReason.UnsupportedContent);
        }
        contents += message.Contents.Count;
        if (count > Messages || contents > Contents)
        {
            throw Error(JevAdapterFailureReason.StateLimitExceeded);
        }
        foreach (var item in message.Contents)
        {
            if (item is null || (item is TextContent t && !JsonBudget.Valid(t.Text)))
            {
                throw Error(JevAdapterFailureReason.UnsupportedContent);
            }
        }
    }

    public JevContent Extract(IReadOnlyList<ChatMessage> request, ChatResponse? response, Pipeline run)
    {
        run.Stage = JevAdapterStage.StateProjection;
        long contents = 0;
        long count = 0;
        foreach (var message in request)
        {
            run.Check();
            Validate(message, ref contents, ++count);
        }
        if (response is not null)
        {
            foreach (var message in response.Messages)
            {
                run.Check();
                Validate(message, ref contents, ++count);
                if (_guardOutput && _scope != JevChatStateScope.Custom && message.Role != ChatRole.Assistant
                    && message.Contents.Any(c => c is TextContent { Text.Length: > 0 }))
                {
                    throw Error(JevAdapterFailureReason.UnsupportedContent);
                }
            }
        }

        if (_projector is not null)
        {
            JevContent projected;
            try { projected = _projector(request, response); }
            catch (Exception ex) when (Failures.Recoverable(ex))
            {
                run.Check();
                throw Failures.From(ex, run.Stage, JevAdapterFailureReason.InvalidProjection, projection: true);
            }
            return Checked(projected);
        }

        var bytes = new JsonBudget(_bytes, run.Stage, JevAdapterFailureReason.StateLimitExceeded);
        using var writer = new Utf8JsonWriter(bytes, JsonBudget.Format);
        writer.WriteStartObject();
        if (_scope != JevChatStateScope.ResponseText)
        {
            var last = request.LastOrDefault(m => m.Role == ChatRole.User);
            IEnumerable<ChatMessage> selected = _scope == JevChatStateScope.ConversationText
                ? request.Where(m => _roles.Contains(m.Role)) : last is null ? [] : [last];
            WriteMessages(writer, "request", selected);
        }
        if (_scope is JevChatStateScope.ResponseText or JevChatStateScope.RequestAndResponseText)
        {
            WriteMessages(writer, "response", response!.Messages.Where(m => m.Role == ChatRole.Assistant));
        }
        writer.WriteEndObject();
        writer.Flush();
        return JevContent.FromJson(bytes.Written.Span);
    }

    public JevContent Checked(JevContent content)
    {
        if (content.IsUnspecified || content.Kind == JevContentKind.Null || (content.TryGetText(out var text) && string.IsNullOrWhiteSpace(text)))
        {
            throw Error(JevAdapterFailureReason.NoAnalyzableText);
        }
        if (content.TryGetText(out text))
        {
            if (!JsonBudget.Valid(text)) { throw Error(JevAdapterFailureReason.UnsupportedContent); }
            if (text.Length > _bytes) { throw Error(JevAdapterFailureReason.StateLimitExceeded); }
        }
        var bytes = new JsonBudget(_bytes, JevAdapterStage.StateProjection, JevAdapterFailureReason.StateLimitExceeded);
        using var writer = new Utf8JsonWriter(bytes, JsonBudget.Format);
        content.WriteTo(writer);
        writer.Flush();
        return content;
    }

    private void WriteMessages(Utf8JsonWriter writer, string name, IEnumerable<ChatMessage> messages)
    {
        var any = false;
        writer.WriteStartArray(name);
        foreach (var message in messages)
        {
            var role = message.Role;
            var value = role == ChatRole.User ? "user" : role == ChatRole.Assistant ? "assistant"
                : role == ChatRole.System ? "system" : role == ChatRole.Tool ? "tool" : role.Value;
            if (value.Length > _bytes) { throw Error(JevAdapterFailureReason.StateLimitExceeded); }
            writer.WriteStartObject();
            writer.WriteString("role", value);
            var text = new StringBuilder();
            foreach (var item in message.Contents)
            {
                if (item is TextContent part)
                {
                    if (part.Text.Length > _bytes - text.Length) { throw Error(JevAdapterFailureReason.StateLimitExceeded); }
                    text.Append(part.Text);
                    any |= !string.IsNullOrWhiteSpace(part.Text);
                }
                else if (item is not UsageContent && _behavior == JevUnsupportedContentBehavior.Reject)
                {
                    throw Error(JevAdapterFailureReason.UnsupportedContent);
                }
            }
            writer.WriteString("text", text.ToString());
            writer.WriteEndObject();
            writer.Flush();
        }
        writer.WriteEndArray();
        if (!any) { throw Error(JevAdapterFailureReason.NoAnalyzableText); }
    }
}
