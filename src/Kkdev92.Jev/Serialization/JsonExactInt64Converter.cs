using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kkdev92.Jev.Serialization;

/// <summary>
/// Reads a <c>long</c> from any JSON spelling of an exact integer, and never through a <c>double</c>.
/// </summary>
/// <remarks>
/// The built-in converter accepts <c>120</c> and refuses <c>120.0</c>, which JSON Schema's
/// <c>integer</c> admits. Referenced by the generated wire models wherever the contract says
/// <c>integer</c>, so the source-generated path and the handwritten reader agree on what a token
/// count is.
/// </remarks>
internal sealed class JsonExactInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number)
        {
            throw new JsonException("Expected a JSON number.");
        }

        // A number token is never escaped, but it can straddle segments when the input is a sequence.
        var token = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;

        return JsonExactInteger.TryParseInt64(token, out var value)
            ? value
            : throw new JsonException("Expected an exact integer that fits in 64 bits.");
    }

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value);
    }
}
