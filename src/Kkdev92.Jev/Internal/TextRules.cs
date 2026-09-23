namespace Kkdev92.Jev.Internal;

/// <summary>
/// Rules about text that has to cross the wire unchanged.
/// </summary>
/// <remarks>
/// <para>
/// A lone surrogate is legal in a .NET string and meaningless in UTF-8. <c>Utf8JsonWriter</c> does
/// not refuse one: it writes U+FFFD in its place, for values and for property names alike (as of
/// .NET 10.0.12). For free text that corrupts what was sent without a word. For a
/// question id or an option label it is worse, because the answer comes back under the replaced
/// name and can no longer be matched to the question that asked it. So text is checked before it
/// is written, and refused rather than repaired.
/// </para>
/// <para>
/// Nothing here normalises, trims or changes case. Text that passes is sent exactly as given.
/// </para>
/// </remarks>
internal static class TextRules
{
    /// <summary>True when the text is well-formed UTF-16: every surrogate is half of a pair.</summary>
    public static bool IsWellFormed(ReadOnlySpan<char> text)
    {
        var index = text.IndexOfAnyInRange('\uD800', '\uDFFF');

        while (index >= 0)
        {
            if (!char.IsHighSurrogate(text[index]) || index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
            {
                return false;
            }

            var next = text[(index + 2)..].IndexOfAnyInRange('\uD800', '\uDFFF');
            index = next < 0 ? -1 : index + 2 + next;
        }

        return true;
    }

    /// <summary>Throws when the text contains a lone surrogate. The message never contains the text.</summary>
    public static void ThrowIfMalformed(ReadOnlySpan<char> text, string paramName, string what)
    {
        if (!IsWellFormed(text))
        {
            throw new ArgumentException($"The {what} contains a lone surrogate, which has no UTF-8 encoding and would be replaced in transit.", paramName);
        }
    }

    /// <summary>
    /// True when the text can be sent as an API key: printable ASCII, no whitespace, not empty.
    /// </summary>
    /// <remarks>
    /// Anything else is either a mistake — a trailing newline from a file, a pasted quote — or an
    /// attempt to smuggle a header line, and in neither case is sending it the right answer.
    /// </remarks>
    public static bool IsUsableApiKey(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (c is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
