using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Unicode;
using Kkdev92.Jev.Internal;
using Kkdev92.Jev.Serialization;

namespace Kkdev92.Jev;

/// <summary>
/// A piece of content the API accepts — a state, an instruction, a description — held in the
/// form it will be sent in.
/// </summary>
/// <remarks>
/// <para>
/// The API takes text or structured JSON almost everywhere, and the difference matters: the
/// string <c>{"x":1}</c> and the object <c>{"x":1}</c> are two different inputs. So text stays
/// text and JSON stays JSON; nothing here parses a string that happens to look like JSON.
/// </para>
/// <para>
/// Four states can be told apart, because the contract tells them apart:
/// <see cref="JevContentKind.Unspecified"/> (<c>default</c>, which leaves the field out),
/// <see cref="JevContentKind.Null"/> (an explicit JSON <c>null</c>), text, and a JSON object or
/// array. None of them is ever turned into another — least of all into an empty string. Which of
/// them a field accepts is checked where the content is used.
/// </para>
/// <para>
/// JSON content is validated once, on creation, and kept as its own copy of compact UTF-8. It
/// holds no reference to the buffer it came from, so the caller is free to reuse that buffer, and
/// a value created once can be sent any number of times without being parsed again.
/// </para>
/// <para>
/// <see cref="ToString"/> reports the kind and size, never the content: an instruction can quote
/// the data it is about.
/// </para>
/// </remarks>
[DebuggerDisplay("{ToString(),nq}")]
public readonly struct JevContent : IEquatable<JevContent>
{
    /// <summary>
    /// How deeply JSON content may nest. Embedded where the API expects it — at most four levels
    /// into a request, or echoed back four levels into a score legend — a value this deep keeps the
    /// whole document within the 64 levels the SDK reads and writes.
    /// </summary>
    public const int MaxDepth = 60;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = MaxDepth,
    };

    // A string for text, a byte[] of compact UTF-8 JSON for an object or an array, nothing otherwise.
    private readonly object? _value;

    private JevContent(JevContentKind kind, object? value)
    {
        Kind = kind;
        _value = value;
    }

    /// <summary>An explicit JSON <c>null</c>. Different from <c>default</c>, which leaves the field out.</summary>
    public static JevContent Null { get; } = new(JevContentKind.Null, null);

    /// <summary>What this content is.</summary>
    public JevContentKind Kind { get; }

    /// <summary>True for <c>default(JevContent)</c>: nothing was given, and the field will be left out where the API allows that.</summary>
    public bool IsUnspecified => Kind == JevContentKind.Unspecified;

    /// <summary>Text, sent as a JSON string.</summary>
    /// <param name="text">The text. Sent exactly as given: nothing is trimmed, normalised or re-cased.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is <see langword="null"/>.</exception>
    public static JevContent FromText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new JevContent(JevContentKind.Text, text);
    }

    /// <summary>
    /// Text, or <see cref="Null"/> for a <see langword="null"/> string.
    /// </summary>
    /// <remarks>
    /// Lets a description or an instruction be written as a string literal. A <see langword="null"/>
    /// string becomes an explicit JSON <c>null</c> — which is how an option without a description
    /// is written — and never an empty string. The conversion does not validate; the text is
    /// checked where it is used.
    /// </remarks>
    public static implicit operator JevContent(string? text)
        => text is null ? Null : new JevContent(JevContentKind.Text, text);

    /// <summary>Parses one JSON value from UTF-8 and keeps a compact, independent copy.</summary>
    /// <param name="utf8Json">A single JSON value: a string, an object, an array or <c>null</c>.</param>
    /// <exception cref="ArgumentException">
    /// The input is not valid UTF-8, is not exactly one JSON value, repeats a property name, nests
    /// deeper than <see cref="MaxDepth"/>, or is a number or a boolean. The message never quotes the
    /// input.
    /// </exception>
    public static JevContent FromJson(ReadOnlySpan<byte> utf8Json)
    {
        // JsonDocument accepts invalid UTF-8 inside a string and only fails when that string is
        // decoded, if it ever is — measured on .NET 10.0.12. Checked here, for the whole input.
        if (!Utf8.IsValid(utf8Json))
        {
            throw new ArgumentException("The JSON is not valid UTF-8.", nameof(utf8Json));
        }

        JsonElement element;

        try
        {
            element = JsonElement.Parse(utf8Json, DocumentOptions);
        }
        catch (JsonException)
        {
            // Not rethrown as the inner exception: its message can quote the input.
            throw new ArgumentException(
                $"The input is not a single valid JSON value, repeats a property name, or nests deeper than {MaxDepth.ToString(CultureInfo.InvariantCulture)} levels.",
                nameof(utf8Json));
        }

        return FromValidatedElement(element, nameof(utf8Json));
    }

    /// <summary>Parses one JSON value from a string and keeps a compact, independent copy.</summary>
    /// <param name="json">A single JSON value: a string, an object, an array or <c>null</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="json"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">See <see cref="FromJson(ReadOnlySpan{byte})"/>.</exception>
    public static JevContent FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (!TextRules.IsWellFormed(json))
        {
            throw new ArgumentException("The JSON contains a lone surrogate.", nameof(json));
        }

        var length = System.Text.Encoding.UTF8.GetByteCount(json);
        var rented = ArrayPool<byte>.Shared.Rent(length);

        try
        {
            var written = System.Text.Encoding.UTF8.GetBytes(json, rented);
            return FromJson(rented.AsSpan(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    /// <summary>Serializes a value with its source-generated metadata, validates the result and keeps it.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="typeInfo">
    /// The metadata to serialize it with — from a <c>JsonSerializerContext</c> of your own. Required
    /// rather than inferred, because inferring it would need reflection, which is not available
    /// under Native AOT.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="typeInfo"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The value serializes to a number or a boolean, or breaks a rule of <see cref="FromJson(ReadOnlySpan{byte})"/>.</exception>
    public static JevContent FromJson<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        return FromJson(bytes);
    }

    /// <summary>Keeps an independent, validated copy of a <see cref="JsonElement"/>.</summary>
    /// <param name="element">A string, an object, an array or <c>null</c>.</param>
    /// <exception cref="ArgumentException">The element is undefined, or breaks a rule of <see cref="FromJson(ReadOnlySpan{byte})"/>.</exception>
    public static JevContent FromJsonElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("The element is undefined.", nameof(element));
        }

        // Re-validated rather than trusted: the element may come from a document parsed with
        // duplicate properties allowed, or nest deeper than the limit.
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, WireEncoding.WriterOptions))
        {
            element.WriteTo(writer);
        }

        return FromJson(buffer.WrittenSpan);
    }

    /// <summary>Gets the text, when this content is text.</summary>
    /// <param name="text">The text, or <see langword="null"/>.</param>
    /// <returns>True when <see cref="Kind"/> is <see cref="JevContentKind.Text"/>.</returns>
    public bool TryGetText([NotNullWhen(true)] out string? text)
    {
        text = Kind == JevContentKind.Text ? (string)_value! : null;
        return text is not null;
    }

    /// <summary>A <see cref="JsonElement"/> copy of the content, for inspection.</summary>
    /// <exception cref="InvalidOperationException">The content is unspecified.</exception>
    public JsonElement ToJsonElement()
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, WireEncoding.WriterOptions))
        {
            WriteTo(writer);
        }

        return JsonElement.Parse(buffer.WrittenSpan);
    }

    /// <summary>Writes the content as one JSON value.</summary>
    /// <param name="writer">The writer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The content is unspecified: there is no value to write.</exception>
    public void WriteTo(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        switch (Kind)
        {
            case JevContentKind.Text:
                writer.WriteStringValue((string)_value!);
                break;
            case JevContentKind.Object:
            case JevContentKind.Array:
                // Written by this type after it was parsed and validated, so skipping the writer's
                // own validation is sound; nothing a caller supplies reaches this unchecked.
                writer.WriteRawValue((byte[])_value!, skipInputValidation: true);
                break;
            case JevContentKind.Null:
                writer.WriteNullValue();
                break;
            case JevContentKind.Unspecified:
            default:
                throw new InvalidOperationException("Unspecified content has no value to write.");
        }
    }

    /// <summary>The text, for the SDK's own writers.</summary>
    internal string? Text => Kind == JevContentKind.Text ? (string)_value! : null;

    /// <summary>True when the text, if any, can be sent unchanged.</summary>
    internal bool IsWellFormed => Kind != JevContentKind.Text || TextRules.IsWellFormed((string)_value!);

    /// <inheritdoc />
    public bool Equals(JevContent other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            JevContentKind.Text => string.Equals((string)_value!, (string)other._value!, StringComparison.Ordinal),
            JevContentKind.Object or JevContentKind.Array => ((byte[])_value!).AsSpan().SequenceEqual((byte[])other._value!),
            _ => true,
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JevContent other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);

        switch (Kind)
        {
            case JevContentKind.Text:
                hash.Add((string)_value!, StringComparer.Ordinal);
                break;
            case JevContentKind.Object:
            case JevContentKind.Array:
                hash.AddBytes((byte[])_value!);
                break;
        }

        return hash.ToHashCode();
    }

    /// <summary>The kind and size of the content. Never the content itself.</summary>
    public override string ToString() => Kind switch
    {
        JevContentKind.Text => $"Text ({((string)_value!).Length.ToString(CultureInfo.InvariantCulture)} chars)",
        JevContentKind.Object => $"Object ({((byte[])_value!).Length.ToString(CultureInfo.InvariantCulture)} bytes)",
        JevContentKind.Array => $"Array ({((byte[])_value!).Length.ToString(CultureInfo.InvariantCulture)} bytes)",
        JevContentKind.Null => "Null",
        _ => "Unspecified",
    };

    /// <inheritdoc cref="Equals(JevContent)" />
    public static bool operator ==(JevContent left, JevContent right) => left.Equals(right);

    /// <summary>Whether two contents differ.</summary>
    public static bool operator !=(JevContent left, JevContent right) => !left.Equals(right);

    private static JevContent FromValidatedElement(JsonElement element, string paramName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var text = element.GetString()!;

                    // An escaped lone surrogate ("\ud800") survives parsing and would be replaced on the
                    // way out.
                    return TextRules.IsWellFormed(text)
                        ? new JevContent(JevContentKind.Text, text)
                        : throw new ArgumentException("The JSON string contains a lone surrogate.", paramName);
                }

            case JsonValueKind.Null:
                return Null;

            case JsonValueKind.Object:
            case JsonValueKind.Array:
                {
                    var buffer = new ArrayBufferWriter<byte>();

                    try
                    {
                        using var writer = new Utf8JsonWriter(buffer, WireEncoding.WriterOptions);
                        element.WriteTo(writer);
                    }
                    catch (InvalidOperationException)
                    {
                        // Re-encoding a string that unescapes to a lone surrogate fails here.
                        throw new ArgumentException("The JSON contains a string with a lone surrogate.", paramName);
                    }

                    return new JevContent(
                        element.ValueKind == JsonValueKind.Object ? JevContentKind.Object : JevContentKind.Array,
                        buffer.WrittenSpan.ToArray());
                }

            default:
                // The API never accepts a bare number or boolean where content goes. Numbers and
                // booleans inside an object or an array are kept as they are.
                throw new ArgumentException($"Content must be a JSON string, object, array or null, not {element.ValueKind}.", paramName);
        }
    }
}

/// <summary>What a <see cref="JevContent"/> holds.</summary>
public enum JevContentKind
{
    /// <summary>Nothing: <c>default(JevContent)</c>. The field is left out where the API allows it.</summary>
    Unspecified = 0,

    /// <summary>Text, sent as a JSON string.</summary>
    Text,

    /// <summary>A JSON object.</summary>
    Object,

    /// <summary>A JSON array.</summary>
    Array,

    /// <summary>An explicit JSON <c>null</c>.</summary>
    Null,
}
