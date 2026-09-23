using System.Globalization;
using System.Text;

namespace Kkdev92.Jev.Tests.Fuzzing;

/// <summary>
/// A JSON value that can say what <c>System.Text.Json</c>'s own models cannot: a member twice, a
/// number spelled any way at all, a string escaped any way at all, a token that is not JSON.
/// </summary>
/// <remarks>
/// The fuzzer needs exactly those. A duplicated field, a number written <c>5E-1</c> or <c>01</c>, a
/// property name spelled with escapes — each is a way a response can differ from the canonical
/// one, and a <c>JsonNode</c> would normalise every one of them away before a decoder saw it.
/// </remarks>
internal abstract class FuzzNode
{
    public abstract FuzzNode Clone();
}

/// <summary>What an object is in a <c>POST /v1/systemone</c> response.</summary>
internal enum FuzzRole
{
    /// <summary>JSON the contract says nothing about: an unknown field's value, a legend's contents.</summary>
    Free,

    Root,

    /// <summary>A map from question id to answer.</summary>
    Answers,

    Answer,

    Usage,

    /// <summary>A map from option label to probability.</summary>
    ChoiceProbabilities,

    /// <summary>A map from level key to probability.</summary>
    LevelProbabilities,

    /// <summary>A map from level key to description.</summary>
    Legend,
}

internal sealed class FuzzObject(FuzzRole role = FuzzRole.Free) : FuzzNode
{
    public FuzzRole Role { get; } = role;

    /// <summary>The question an answer, or one of its maps, belongs to.</summary>
    public Planning.QuestionDefinition? Question { get; init; }

    public List<FuzzMember> Members { get; } = [];

    /// <summary>A comma after the last member, which JSON does not allow.</summary>
    public bool TrailingComma { get; set; }

    /// <summary>
    /// True for a map, whose member names are keys the service chose — answer ids, labels, level
    /// keys — rather than fields of the contract. An unknown key in a map is a fault, not an extension.
    /// </summary>
    public bool IsMap => Role is FuzzRole.Answers or FuzzRole.ChoiceProbabilities or FuzzRole.LevelProbabilities or FuzzRole.Legend;

    public FuzzObject Add(string name, FuzzNode value)
    {
        Members.Add(new FuzzMember(name, value));
        return this;
    }

    public FuzzMember? Find(string name) => Members.Find(m => m.Name == name);

    public override FuzzNode Clone()
    {
        var copy = new FuzzObject(Role) { Question = Question, TrailingComma = TrailingComma };

        foreach (var member in Members)
        {
            copy.Members.Add(member with { Value = member.Value.Clone() });
        }

        return copy;
    }
}

/// <summary>One member of an object.</summary>
/// <param name="Name">The name, unescaped.</param>
/// <param name="Value">The value.</param>
/// <param name="EscapeName">Spell every character of the name as an escape.</param>
/// <param name="RawName">When set, written in place of the name exactly as given, quotes included.</param>
internal sealed record FuzzMember(string Name, FuzzNode Value, bool EscapeName = false, string? RawName = null);

internal sealed class FuzzArray : FuzzNode
{
    public List<FuzzNode> Items { get; } = [];

    /// <summary>A comma after the last item, which JSON does not allow.</summary>
    public bool TrailingComma { get; set; }

    public override FuzzNode Clone()
    {
        var copy = new FuzzArray { TrailingComma = TrailingComma };
        copy.Items.AddRange(Items.Select(i => i.Clone()));
        return copy;
    }
}

internal sealed class FuzzString(string value) : FuzzNode
{
    public string Value { get; } = value;

    /// <summary>Spell every character as an escape.</summary>
    public bool Escape { get; set; }

    public override FuzzNode Clone() => new FuzzString(Value) { Escape = Escape };
}

/// <summary>A number — or any other token, valid or not — written exactly as given.</summary>
internal sealed class FuzzRaw(string text) : FuzzNode
{
    public string Text { get; } = text;

    public static FuzzRaw Of(double value) => new(value.ToString("R", CultureInfo.InvariantCulture));

    public static FuzzRaw Of(long value) => new(value.ToString(CultureInfo.InvariantCulture));

    public override FuzzNode Clone() => new FuzzRaw(Text);
}

/// <summary>Writes a <see cref="FuzzNode"/> as JSON text, varying what JSON lets vary when asked to.</summary>
/// <remarks>
/// With <c>varyLayout</c> off the output is canonical: no whitespace, and only what JSON requires
/// escaped. With it on, insignificant whitespace appears between tokens and non-ASCII characters
/// are escaped at random — neither of which may change what a decoder reads.
/// </remarks>
internal sealed class FuzzJsonWriter
{
    private static readonly char Backslash = (char)92;

    private readonly StringBuilder _text = new();
    private readonly Random _random;
    private readonly bool _varyLayout;

    private FuzzJsonWriter(Random random, bool varyLayout)
    {
        _random = random;
        _varyLayout = varyLayout;
    }

    public static string Write(FuzzNode node, Random random, bool varyLayout)
    {
        var writer = new FuzzJsonWriter(random, varyLayout);
        writer.Value(node);
        return writer._text.ToString();
    }

    private void Value(FuzzNode node)
    {
        switch (node)
        {
            case FuzzObject obj:
                _text.Append('{');
                Space();

                for (var i = 0; i < obj.Members.Count; i++)
                {
                    if (i > 0)
                    {
                        _text.Append(',');
                        Space();
                    }

                    var member = obj.Members[i];

                    if (member.RawName is not null)
                    {
                        _text.Append(member.RawName);
                    }
                    else
                    {
                        String(member.Name, member.EscapeName);
                    }

                    Space();
                    _text.Append(':');
                    Space();
                    Value(member.Value);
                    Space();
                }

                if (obj.TrailingComma)
                {
                    _text.Append(',');
                }

                _text.Append('}');
                break;

            case FuzzArray array:
                _text.Append('[');
                Space();

                for (var i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        _text.Append(',');
                        Space();
                    }

                    Value(array.Items[i]);
                    Space();
                }

                if (array.TrailingComma)
                {
                    _text.Append(',');
                }

                _text.Append(']');
                break;

            case FuzzString str:
                String(str.Value, str.Escape);
                break;

            case FuzzRaw raw:
                _text.Append(raw.Text);
                break;
        }
    }

    /// <summary>Insignificant whitespace, sometimes, of the four kinds JSON allows.</summary>
    private void Space()
    {
        if (_varyLayout && _random.Next(6) == 0)
        {
            _text.Append(" \t\n\r"[_random.Next(4)]);
        }
    }

    /// <summary>
    /// A string, with every character escaped when asked; otherwise with what JSON requires
    /// escaped, and — when the layout varies — non-ASCII characters escaped at random.
    /// </summary>
    /// <remarks>
    /// A surrogate pair is escaped whole or not at all, and a lone surrogate is always escaped: a
    /// lone surrogate written as a character would be replaced when the text is encoded, and the
    /// body would no longer say what the case meant it to.
    /// </remarks>
    private void String(string value, bool escapeAll)
    {
        _text.Append('"');

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                if (escapeAll || (_varyLayout && _random.Next(2) == 0))
                {
                    Escaped(c);
                    Escaped(value[i + 1]);
                }
                else
                {
                    _text.Append(c).Append(value[i + 1]);
                }

                i++;
            }
            else if (escapeAll || char.IsSurrogate(c) || c < ' ' || (_varyLayout && c > 127 && _random.Next(2) == 0))
            {
                Escaped(c);
            }
            else if (c == '"' || c == Backslash)
            {
                _text.Append(Backslash).Append(c);
            }
            else
            {
                _text.Append(c);
            }
        }

        _text.Append('"');
    }

    private void Escaped(char c)
        => _text.Append(Backslash).Append('u').Append(((int)c).ToString(_random.Next(2) == 0 ? "x4" : "X4", CultureInfo.InvariantCulture));
}
