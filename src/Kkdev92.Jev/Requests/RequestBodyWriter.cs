using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Kkdev92.Jev.Buffers;
using Kkdev92.Jev.Serialization;

namespace Kkdev92.Jev.Requests;

/// <summary>The state of one evaluation, in whichever of the three forms the caller gave it.</summary>
/// <remarks>
/// A struct over either a <see cref="JevContent"/> or a typed writer, so the text and prepared-content
/// paths allocate nothing to describe their state, and the generic one allocates one small object.
/// Keeping the rest of the pipeline non-generic means one copy of it in a Native AOT binary, however
/// many state types a program sends.
/// </remarks>
internal readonly struct StateSource
{
    private StateSource(JevContent content, TypedState? typed)
    {
        Content = content;
        Typed = typed;
    }

    public JevContent Content { get; }

    public TypedState? Typed { get; }

    public static StateSource FromContent(JevContent content) => new(content, null);

    public static StateSource FromTyped<TState>(TState value, JsonTypeInfo<TState> typeInfo) => new(default, new TypedState<TState>(value, typeInfo));
}

/// <summary>A state serialized with metadata the caller supplied.</summary>
internal abstract class TypedState
{
    public abstract void Write(Utf8JsonWriter writer);
}

/// <inheritdoc />
internal sealed class TypedState<TState>(TState value, JsonTypeInfo<TState> typeInfo) : TypedState
{
    public override void Write(Utf8JsonWriter writer) => JsonSerializer.Serialize(writer, value, typeInfo);
}

/// <summary>
/// Writes <c>{"model":…,"state":…,"questions":…}</c> once per call.
/// </summary>
/// <remarks>
/// <para>
/// The <c>questions</c> object was encoded when the plan was built, and is copied in as bytes. That
/// is the one place the writer's validation is skipped, and it is sound only because those bytes
/// were produced by the plan builder from content it had already validated. Nothing a caller passes
/// at call time is written without validation.
/// </para>
/// <para>
/// The body is written once and the same bytes are sent by every attempt of the call: a retry never
/// re-runs a caller's converter or re-reads a state object that might have changed in between.
/// </para>
/// </remarks>
internal static class RequestBodyWriter
{
    /// <exception cref="JevLimitException">The body would exceed <paramref name="limit"/> bytes.</exception>
    /// <exception cref="ArgumentException">A typed state serialized to something other than an object, an array or a string.</exception>
    /// <returns>The body, in an array of exactly its length that the call owns.</returns>
    public static byte[] WriteEvaluate(string model, StateSource state, JevDecisionPlan plan, int limit)
    {
        var text = state.Content.Text;

        // Every character is at least one byte, so text longer than the limit cannot fit. Refused
        // here rather than after the writer has asked for room for three bytes a character.
        if (text is not null && text.Length > limit)
        {
            throw new JevLimitException(JevOperation.Evaluate, JevLimit.RequestBody, limit, JevRequestTransmission.NotStarted);
        }

        // Room for the fixed members, the encoded questions and the state as the writer will ask
        // for it: a string is requested at three bytes a character, before it is known to need fewer.
        var estimate = (int)Math.Min(128L + (model.Length * 3L) + plan.EncodedQuestionsLength + (text is null ? 1024L : (text.Length * 3L) + 3), int.MaxValue);
        using var buffer = new BoundedBufferWriter(estimate, limit, () => throw new JevLimitException(JevOperation.Evaluate, JevLimit.RequestBody, limit, JevRequestTransmission.NotStarted));

        using (var writer = new Utf8JsonWriter(buffer, WireEncoding.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("model"u8, model);
            writer.WritePropertyName("state"u8);

            if (state.Typed is { } typed)
            {
                writer.Flush();
                var start = buffer.WrittenCount;

                typed.Write(writer);
                writer.Flush();

                // The writer emits no whitespace, so the first byte after the property name is the
                // first byte of the value, and it says what the value is.
                if (buffer.WrittenCount <= start || buffer.WrittenSpan[start] is not ((byte)'{' or (byte)'[' or (byte)'"'))
                {
                    throw new ArgumentException("The state must serialize to a JSON object, array or string. The API does not accept a number, a boolean or null as the state.", "state");
                }
            }
            else
            {
                state.Content.WriteTo(writer);
            }

            writer.WritePropertyName("questions"u8);
            writer.WriteRawValue(plan.EncodedQuestions, skipInputValidation: true);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
