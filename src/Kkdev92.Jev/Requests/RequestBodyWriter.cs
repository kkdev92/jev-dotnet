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
/// Writes <c>{"model":…,"state":…,"questions":…}</c> once per call, with the <c>assets</c> that
/// name the request's images when it has any.
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
/// <para>
/// With images, the JSON becomes the first part of a form and each image a part after it. The limit
/// is on the whole form, so the JSON is held to what the images and the parts' headers leave.
/// </para>
/// </remarks>
internal static class RequestBodyWriter
{
    /// <param name="model">The model to ask.</param>
    /// <param name="state">The state.</param>
    /// <param name="plan">The questions, and the images they show.</param>
    /// <param name="callImages">The images the call passed, already checked and without the plan's own.</param>
    /// <param name="limit">The most bytes the whole body may take.</param>
    /// <exception cref="JevLimitException">The body would exceed <paramref name="limit"/> bytes.</exception>
    /// <exception cref="ArgumentException">
    /// A typed state serialized to something other than an object, an array or a string, or does not
    /// reference the request's images as <see cref="RequestImages"/> requires.
    /// </exception>
    /// <returns>The body, which the call owns.</returns>
    public static RequestBody WriteEvaluate(string model, StateSource state, JevDecisionPlan plan, JevImage[] callImages, int limit)
    {
        // The plan's images first, so a plan's parts are numbered the same in every call.
        JevImage[] images = callImages.Length == 0 ? plan.Images
            : plan.Images.Length == 0 ? callImages
            : [.. plan.Images, .. callImages];

        var form = images.Length == 0 ? null : new FormLayout(images);
        var jsonLimit = form is null ? limit : limit - form.Overhead - form.ImageBytes;
        var text = state.Content.Text;

        // Every character is at least one byte, so text longer than the limit cannot fit. Refused
        // here rather than after the writer has asked for room for three bytes a character.
        if (jsonLimit <= 0 || (text is not null && text.Length > jsonLimit))
        {
            throw LimitExceeded(limit);
        }

        // Room for the fixed members, the encoded questions, the assets and the state as the writer
        // will ask for it: a string is requested at three bytes a character, before it is known to
        // need fewer.
        var estimate = (int)Math.Min(
            128L + (model.Length * 3L) + plan.EncodedQuestionsLength + (images.Length * 96L) + (text is null ? 1024L : (text.Length * 3L) + 3),
            int.MaxValue);

        using var buffer = new BoundedBufferWriter(estimate, (int)jsonLimit, () => throw LimitExceeded(limit));

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

                // A typed state is only text once it is written; a state given as text or content
                // was checked before the call started.
                RequestImages.CheckState(buffer.WrittenSpan[start..], plan, callImages);
            }
            else
            {
                state.Content.WriteTo(writer);
            }

            writer.WritePropertyName("questions"u8);
            writer.WriteRawValue(plan.EncodedQuestions, skipInputValidation: true);

            if (form is not null)
            {
                WriteAssets(writer, form);
            }

            writer.WriteEndObject();
        }

        var json = buffer.ToArray();
        return form is null ? RequestBody.FromJson(json) : form.Complete(json);
    }

    /// <summary>
    /// Writes <c>"assets":{"&lt;reference&gt;":{"type":"image","part":"image-1"},…}</c>: each image
    /// under its reference, pointing at the form part that carries it.
    /// </summary>
    private static void WriteAssets(Utf8JsonWriter writer, FormLayout form)
    {
        writer.WriteStartObject("assets"u8);

        for (var i = 0; i < form.Images.Length; i++)
        {
            writer.WriteStartObject(form.Images[i].Reference);
            writer.WriteString("type"u8, "image"u8);
            writer.WriteString("part"u8, form.PartNames[i]);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static JevLimitException LimitExceeded(int limit)
        => new(JevOperation.Evaluate, JevLimit.RequestBody, limit, JevRequestTransmission.NotStarted);
}
