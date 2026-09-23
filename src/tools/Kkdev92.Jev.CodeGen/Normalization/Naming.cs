using System.Text;

namespace Kkdev92.Jev.CodeGen.Normalization;

/// <summary>
/// Turns wire names into C# identifiers by a fixed rule.
/// </summary>
/// <remarks>
/// <para>
/// The rule is deliberately small: split on the separators the contract uses, upper-case the first
/// letter of each part, leave everything else alone. <c>input_tokens</c> becomes
/// <c>InputTokens</c>; <c>true</c> becomes <c>True</c>. The wire name itself is never derived back
/// from the C# name, so nothing here can change what goes over the wire.
/// </para>
/// <para>
/// When two wire names would become the same identifier — <c>a-b</c> and <c>a_b</c> — generation
/// fails. Resolving that with a numeric suffix would make the name of a member depend on which of
/// the two the document happened to list first.
/// </para>
/// </remarks>
internal static class Naming
{
    /// <summary>
    /// Reserved words. A PascalCase identifier cannot collide with one of these today, but the rule
    /// is checked rather than assumed, because a wire name that is already capitalised passes
    /// through unchanged.
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
        "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
        "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
        // Contextual keywords that are legal identifiers but read badly as generated member names,
        // including C# 14's 'field', which inside an accessor means something else.
        "field", "value", "var", "dynamic", "record", "required", "scoped", "file", "extension",
    };

    /// <summary>Converts a wire name to a PascalCase identifier.</summary>
    /// <exception cref="InvalidDataException">The name has no letters or digits to build an identifier from.</exception>
    public static string ToPascalCase(string wireName)
    {
        ArgumentNullException.ThrowIfNull(wireName);

        var builder = new StringBuilder(wireName.Length);
        var upperNext = true;

        foreach (var c in wireName)
        {
            if (c is '_' or '-' or '.' or ' ')
            {
                upperNext = true;
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(c))
            {
                throw new InvalidDataException($"'{Printable(wireName)}' contains '{Printable(c.ToString())}', which the naming rule does not map. Add an override.");
            }

            builder.Append(upperNext ? char.ToUpperInvariant(c) : c);
            upperNext = false;
        }

        if (builder.Length == 0)
        {
            throw new InvalidDataException($"'{Printable(wireName)}' has no letters or digits to name a member after. Add an override.");
        }

        if (char.IsAsciiDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        var identifier = builder.ToString();

        return Keywords.Contains(identifier) ? "@" + identifier : identifier;
    }

    /// <summary>True when the text is already a plain PascalCase-shaped identifier the emitter can use as a type name.</summary>
    public static bool IsTypeName(string name)
        => name.Length > 0
            && char.IsAsciiLetterUpper(name[0])
            && name.All(char.IsAsciiLetterOrDigit)
            && !Keywords.Contains(name);

    /// <summary>Renders untrusted text for an error message without letting control characters through.</summary>
    public static string Printable(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var c in text)
        {
            builder.Append(char.IsControl(c) || char.IsSurrogate(c) ? $"\\u{(int)c:X4}" : c.ToString());
        }

        return builder.ToString();
    }
}
