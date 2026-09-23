using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Kkdev92.Jev.Serialization;

/// <summary>
/// How the SDK escapes the JSON it writes: one encoder for every writer, so a string is written the
/// same way whether it is a state, an instruction, a label or part of structured content.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="JavaScriptEncoder.Default"/> escapes every character outside Basic Latin, which turns
/// each three-byte character of Japanese text into a six-byte <c>\uXXXX</c>. Measured with the
/// benchmarks in <c>src/benchmarks</c> on .NET 10.0.12 (win-arm64): with that encoder, a request
/// with a 4 KiB Japanese state takes 8.3 µs to write against 0.74 µs for ASCII of the same size,
/// and comes out about twice as long; with this one it takes 2.0 µs and is the same size as the
/// ASCII one.
/// </para>
/// <para>
/// This encoder allows the whole Basic Multilingual Plane, so such text is written as the UTF-8 it
/// already is. It is not <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>: it still escapes
/// the HTML-sensitive characters (<c>&lt; &gt; &amp; ' "</c>), control characters, and everything
/// outside the plane — most emoji among them — as surrogate pairs. Either way the bytes are valid JSON and
/// the service reads the same string. What changes is the size of the request and the time to
/// write it; not what the model reads, and not the tokens billed.
/// </para>
/// </remarks>
internal static class WireEncoding
{
    /// <summary>The encoder.</summary>
    public static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);

    /// <summary>Options for a writer of content the SDK keeps or sends: this encoder, compact, validated.</summary>
    public static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = Encoder,
        MaxDepth = 64,
        SkipValidation = false,
    };
}
