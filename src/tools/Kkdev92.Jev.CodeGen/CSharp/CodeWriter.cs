using System.Globalization;
using System.Text;

namespace Kkdev92.Jev.CodeGen.CSharp;

/// <summary>
/// Accumulates C# source with LF line endings and four-space indentation, whatever the platform.
/// </summary>
/// <remarks>
/// <c>StringBuilder.AppendLine</c> writes <see cref="Environment.NewLine"/>, which is CRLF on
/// Windows — so it is never used here. Output that differs by operating system would fail
/// <c>codegen verify</c> on one of the two CI runners, which is the point of running it on both.
/// </remarks>
internal sealed class CodeWriter
{
    private readonly StringBuilder _builder = new();
    private int _indent;

    public CodeWriter Line(string text = "")
    {
        if (text.Length > 0)
        {
            _builder.Append(' ', _indent * 4);
            _builder.Append(text);
        }

        _builder.Append('\n');
        return this;
    }

    public CodeWriter Open(string text)
    {
        Line(text);
        Line("{");
        _indent++;
        return this;
    }

    public CodeWriter Close(string suffix = "")
    {
        _indent--;
        Line("}" + suffix);
        return this;
    }

    public override string ToString() => _builder.ToString();

    /// <summary>A C# string literal for arbitrary text, escaped so that no input can end the literal early.</summary>
    public static string Literal(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                default:
                    if (c < ' ' || c > '~')
                    {
                        // Everything outside printable ASCII is escaped, so the generated file is
                        // ASCII whatever the contract contains and no character can read as a
                        // line break or a direction override inside the source.
                        builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
