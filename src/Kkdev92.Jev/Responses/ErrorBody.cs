using System.Globalization;
using System.Text.Json;
using System.Text.Unicode;

namespace Kkdev92.Jev.Responses;

/// <summary>
/// Pulls the machine-readable parts out of an error body, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// Two shapes are known. A 422 carries the contract's <c>HTTPValidationError</c>:
/// <c>{"detail":[{"loc":[…],"msg":…,"type":…}]}</c>. Authentication failures carry
/// <c>{"detail":{"error_type":…,"message":…}}</c>, which is observed rather than documented
/// (<c>missing-key-status</c> in <c>spec/typesafe-v1/semantics.json</c>).
/// </para>
/// <para>
/// From those, only codes are taken — the error type and each validation entry's type and location —
/// and only when they have the shape of a code. <c>message</c>, <c>msg</c>, <c>input</c> and
/// <c>ctx</c> are not read: they are human text or echoes of the request. A body that fits neither
/// shape, is not JSON, or was truncated at the limit yields nothing, and the status still stands.
/// </para>
/// </remarks>
internal static class ErrorBody
{
    /// <summary>More entries than this and the rest are not worth holding.</summary>
    private const int MaxValidationErrors = 64;

    public static (string? ErrorType, IReadOnlyList<JevValidationError> ValidationErrors) Parse(ReadOnlySpan<byte> body)
    {
        if (body.IsEmpty || !Utf8.IsValid(body))
        {
            return (null, []);
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("detail", out var detail))
            {
                return (null, []);
            }

            return detail.ValueKind switch
            {
                JsonValueKind.Object => (ErrorType(detail), []),
                JsonValueKind.Array => (null, ValidationErrors(detail)),
                _ => (null, []),
            };
        }
        catch (JsonException)
        {
            // Truncated at the limit, HTML from a proxy, or empty: the status is the answer.
            return (null, []);
        }
    }

    private static string? ErrorType(JsonElement detail)
        => detail.TryGetProperty("error_type", out var type) && type.ValueKind == JsonValueKind.String && IsCode(type.GetString())
            ? type.GetString()
            : null;

    private static List<JevValidationError> ValidationErrors(JsonElement detail)
    {
        var errors = new List<JevValidationError>();

        foreach (var entry in detail.EnumerateArray())
        {
            if (errors.Count == MaxValidationErrors)
            {
                break;
            }

            if (entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !IsCode(type.GetString()))
            {
                continue;
            }

            var location = new List<string>();

            if (entry.TryGetProperty("loc", out var loc) && loc.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in loc.EnumerateArray())
                {
                    switch (part.ValueKind)
                    {
                        case JsonValueKind.String:
                            location.Add(part.GetString()!);
                            break;
                        case JsonValueKind.Number when part.TryGetInt64(out var index):
                            location.Add(index.ToString(CultureInfo.InvariantCulture));
                            break;
                    }
                }
            }

            errors.Add(new JevValidationError(location, type.GetString()!));
        }

        return errors;
    }

    /// <summary>Lower-case letters, digits and underscores, starting with a letter: the shape of a code, and nothing that could carry text.</summary>
    internal static bool IsCode(string? value)
        => value is { Length: > 0 and <= 64 }
            && char.IsAsciiLetterLower(value[0])
            && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
}
