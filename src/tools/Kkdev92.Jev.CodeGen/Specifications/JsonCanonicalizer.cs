using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Kkdev92.Jev.CodeGen.Specifications;

/// <summary>
/// Rewrites a JSON document into a canonical, byte-stable, reviewable form.
/// </summary>
/// <remarks>
/// <para>
/// The TypeSafe OpenAPI document arrives as a single line of about 14 KB. A contract committed in
/// that form would make every update a one-line diff that nobody can review, which defeats the
/// point of committing a snapshot at all. So the committed contract is written in this form, and a
/// reviewer diffs it line by line.
/// </para>
/// <para>
/// The endpoint serves the same bytes for the same document, so the upstream document's own hash
/// is meaningful, and it is what <c>provenance.json</c> records about it. Canonicalizing is still
/// what makes a diff readable, and what makes the committed contract's hash independent of how the
/// server orders keys.
/// </para>
/// <para>
/// Canonicalizing is not hand-editing: it is deterministic, lossless and applied by the tool.
/// Object keys are ordered with <see cref="StringComparer.Ordinal"/>; array order, numbers and
/// string contents are preserved exactly.
/// </para>
/// <para>
/// The layout is written by hand rather than by an indented <see cref="Utf8JsonWriter"/>, because
/// that writer does not indent a value written raw: a number, which has to be written raw to keep
/// its exact spelling, comes out glued to the bracket before it (<c>[0.9</c>).
/// </para>
/// </remarks>
internal static class JsonCanonicalizer
{
    /// <summary>UTF-8 without a byte order mark, matching the rest of the repository.</summary>
    public static readonly UTF8Encoding Encoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The escaping used for strings and names.
    /// </summary>
    /// <remarks>
    /// The strict default escapes '&lt;', '&gt;', '&amp;', the apostrophe and every non-ASCII
    /// character, which turns "Bearer &lt;API_KEY&gt;" into "Bearer \u003CAPI_KEY\u003E" in a file
    /// that exists to be read. These files are never HTML, so the relaxed encoder's one caveat does
    /// not apply; it is also a fixed algorithm, so the bytes do not depend on the machine.
    /// </remarks>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        // A snapshot with a key written twice has two readings, and which one a parser keeps is
        // an implementation detail. Refused at the door rather than resolved silently.
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 128,
    };

    /// <summary>Returns the canonical UTF-8 bytes for a JSON payload, ending with a newline.</summary>
    /// <exception cref="InvalidDataException">The payload is not valid UTF-8 JSON, or repeats a key.</exception>
    public static byte[] Canonicalize(ReadOnlySpan<byte> utf8Json)
    {
        // JsonDocument does not validate the bytes inside a string token: an invalid sequence is
        // only noticed when the string is decoded, and then only if it is. Checked here, once,
        // for the whole document.
        if (!Utf8.IsValid(utf8Json))
        {
            throw new InvalidDataException("The document is not valid UTF-8.");
        }

        if (utf8Json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            throw new InvalidDataException("The document starts with a byte order mark.");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(utf8Json.ToArray(), ParseOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The document is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            var output = new ArrayBufferWriter<byte>(utf8Json.Length * 2);
            Write(document.RootElement, output, depth: 0);

            // A trailing newline keeps the file POSIX-clean and diff-friendly.
            Append(output, "\n"u8);
            return output.WrittenSpan.ToArray();
        }
    }

    /// <summary>True when the payload is already in canonical form.</summary>
    public static bool IsCanonical(ReadOnlySpan<byte> utf8Json)
    {
        try
        {
            return Canonicalize(utf8Json).AsSpan().SequenceEqual(utf8Json);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static void Write(JsonElement element, ArrayBufferWriter<byte> output, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var properties = element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToList();

                    if (properties.Count == 0)
                    {
                        Append(output, "{}"u8);
                        break;
                    }

                    Append(output, "{\n"u8);

                    for (var i = 0; i < properties.Count; i++)
                    {
                        Indent(output, depth + 1);
                        WriteString(properties[i].Name, output);
                        Append(output, ": "u8);
                        Write(properties[i].Value, output, depth + 1);
                        Append(output, i < properties.Count - 1 ? ",\n"u8 : "\n"u8);
                    }

                    Indent(output, depth);
                    Append(output, "}"u8);
                    break;
                }

            case JsonValueKind.Array:
                {
                    // Array order is semantic (required, anyOf, examples) and is never reordered.
                    var items = element.EnumerateArray().ToList();

                    if (items.Count == 0)
                    {
                        Append(output, "[]"u8);
                        break;
                    }

                    Append(output, "[\n"u8);

                    for (var i = 0; i < items.Count; i++)
                    {
                        Indent(output, depth + 1);
                        Write(items[i], output, depth + 1);
                        Append(output, i < items.Count - 1 ? ",\n"u8 : "\n"u8);
                    }

                    Indent(output, depth);
                    Append(output, "]"u8);
                    break;
                }

            case JsonValueKind.Number:
                // Raw, so that 1.0 stays 1.0 and 1e3 stays 1e3.
                Append(output, Encoding.GetBytes(element.GetRawText()));
                break;

            case JsonValueKind.String:
                WriteString(element.GetString()!, output);
                break;

            case JsonValueKind.True:
                Append(output, "true"u8);
                break;

            case JsonValueKind.False:
                Append(output, "false"u8);
                break;

            case JsonValueKind.Null:
                Append(output, "null"u8);
                break;

            case JsonValueKind.Undefined:
            default:
                throw new InvalidDataException($"Unexpected JSON value kind '{element.ValueKind}'.");
        }
    }

    private static void WriteString(string value, ArrayBufferWriter<byte> output)
    {
        Append(output, "\""u8);
        Append(output, JsonEncodedText.Encode(value, Encoder).EncodedUtf8Bytes);
        Append(output, "\""u8);
    }

    private static void Indent(ArrayBufferWriter<byte> output, int depth)
    {
        var span = output.GetSpan(depth * 2);
        span[..(depth * 2)].Fill((byte)' ');
        output.Advance(depth * 2);
    }

    private static void Append(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> bytes) => output.Write(bytes);
}
