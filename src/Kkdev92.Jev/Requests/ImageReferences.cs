using System.Buffers;
using System.Text.Json;

namespace Kkdev92.Jev.Requests;

/// <summary>
/// The references the SDK makes for images, and finding them in the strings of a request.
/// </summary>
/// <remarks>
/// <para>
/// TypeSafe matches a reference as plain text anywhere in a string value, and never in an object
/// key. A reference here is <c>jev-image-</c> and 32 lowercase hexadecimal digits from a random
/// <see cref="Guid"/>: long enough not to occur in text by accident, and shaped so that the SDK
/// can recognise one of its own references in a request without being told about it. That is what
/// lets it refuse a request that mentions an image without sending it, which the service would
/// otherwise read as the reference's text.
/// </para>
/// <para>
/// Every character of a reference is ASCII that JSON never needs to escape, so the SDK's own
/// writers put it on the wire as it is.
/// </para>
/// </remarks>
internal static class ImageReferences
{
    /// <summary>What every reference starts with.</summary>
    public const string Prefix = "jev-image-";

    /// <summary>The length of a reference: the prefix and 32 digits.</summary>
    public const int Length = 42;

    private const int Digits = 32;

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 128,
    };

    /// <summary>A new reference.</summary>
    public static string Create() => Prefix + Guid.NewGuid().ToString("N");

    /// <summary>Whether text has the prefix of a reference anywhere. Cheap enough for every call.</summary>
    public static bool MayContain(ReadOnlySpan<char> text) => text.Contains(Prefix, StringComparison.Ordinal);

    /// <summary>Whether JSON has the prefix of a reference anywhere in its bytes.</summary>
    /// <remarks>
    /// Looks at the bytes as they are, so a reference spelled with escapes is not seen. That is sound
    /// for the JSON it is given: a state or a question the SDK wrote through its own writer, which
    /// never escapes a letter, a digit or a hyphen — <see cref="JevContent.FromJson(string)"/> writes
    /// its input again rather than keeping it. The one way round it is a converter of the caller's
    /// that writes raw JSON with such escapes, in a typed state or in content made from a typed
    /// value. A reference hidden that way is found only where images are passed or added, which
    /// makes the SDK read every string; elsewhere it reaches the service as text.
    /// </remarks>
    public static bool MayContain(ReadOnlySpan<byte> json) => json.IndexOf("jev-image-"u8) >= 0;

    /// <summary>Adds every reference in a piece of text to <paramref name="found"/>.</summary>
    public static void Collect(ReadOnlySpan<char> text, HashSet<string> found)
    {
        var at = 0;

        while (at < text.Length)
        {
            var index = text[at..].IndexOf(Prefix, StringComparison.Ordinal);

            if (index < 0)
            {
                return;
            }

            var start = at + index;

            if (text.Length - start >= Length && IsDigits(text.Slice(start + Prefix.Length, Digits)))
            {
                found.Add(text.Slice(start, Length).ToString());
                at = start + Length;
            }
            else
            {
                // The prefix cannot overlap itself, so the next one starts after this one.
                at = start + Prefix.Length;
            }
        }
    }

    /// <summary>Adds every reference in the string values of a JSON value to <paramref name="found"/>. Property names are not read: the service does not replace a reference in a key.</summary>
    /// <exception cref="JsonException">The bytes are not one JSON value.</exception>
    public static void CollectFromJson(ReadOnlySpan<byte> json, HashSet<string> found)
    {
        var reader = new Utf8JsonReader(json, ReaderOptions);

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                continue;
            }

            if (!reader.ValueIsEscaped)
            {
                Collect(reader.ValueSpan, found);
                continue;
            }

            // Unescaped text is never longer than its escaped bytes.
            var rented = ArrayPool<byte>.Shared.Rent(reader.ValueSpan.Length);

            try
            {
                var length = reader.CopyString(rented);
                Collect(rented.AsSpan(0, length), found);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static void Collect(ReadOnlySpan<byte> utf8, HashSet<string> found)
    {
        var at = 0;

        while (at < utf8.Length)
        {
            var index = utf8[at..].IndexOf("jev-image-"u8);

            if (index < 0)
            {
                return;
            }

            var start = at + index;

            if (utf8.Length - start >= Length && IsDigits(utf8.Slice(start + Prefix.Length, Digits)))
            {
                found.Add(System.Text.Encoding.ASCII.GetString(utf8.Slice(start, Length)));
                at = start + Length;
            }
            else
            {
                at = start + Prefix.Length;
            }
        }
    }

    private static bool IsDigits(ReadOnlySpan<char> digits) => !digits.ContainsAnyExcept("0123456789abcdef");

    private static bool IsDigits(ReadOnlySpan<byte> digits) => !digits.ContainsAnyExcept("0123456789abcdef"u8);
}
