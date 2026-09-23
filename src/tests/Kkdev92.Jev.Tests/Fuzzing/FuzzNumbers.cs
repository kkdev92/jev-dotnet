using System.Globalization;
using System.Text;

namespace Kkdev92.Jev.Tests.Fuzzing;

/// <summary>Spells a JSON number another way without changing the value it denotes.</summary>
/// <remarks>
/// <c>0.5</c>, <c>5e-1</c>, <c>0.50</c>, <c>50E-2</c> and <c>5.0e-01</c> are one decimal value, and a
/// correctly rounded parser turns each into the same <see cref="double"/>; <c>120</c>, <c>1.2e2</c>
/// and <c>120.0</c> are one integer. A decoder that reads any of them differently is wrong.
/// </remarks>
internal static class FuzzNumbers
{
    /// <summary>Another spelling of <paramref name="raw"/>, or null when it is not a JSON number this can respell.</summary>
    public static string? Respell(string raw, Random random)
    {
        if (!TryParse(raw, out var negative, out var digits, out var exponent))
        {
            return null;
        }

        var text = new StringBuilder(negative ? "-" : string.Empty);

        if (digits.Length == 0)
        {
            // Zero, whose exponent means nothing.
            text.Append(random.Next(4) switch
            {
                0 => "0",
                1 => "0.000",
                2 => "0" + Exponent(random.Next(-400, 400), random),
                _ => "0.0" + Exponent(random.Next(-5, 5), random),
            });

            return text.ToString();
        }

        switch (random.Next(4))
        {
            case 0:
                // All the digits, then the exponent: 5e-1.
                text.Append(digits).Append(Exponent(exponent, random));
                break;

            case 1:
                Positional(text, digits, exponent, random);
                break;

            case 2:
                // One digit before the point: 5.0e-1, 1.2E+2.
                text.Append(digits[0]);

                if (digits.Length > 1 || random.Next(2) == 0)
                {
                    text.Append('.').Append(digits.Length > 1 ? digits[1..] : "0");
                }

                text.Append(Exponent(exponent + digits.Length - 1, random));
                break;

            default:
                // Zeros added to the digits and taken back by the exponent: 500e-3.
                var zeros = random.Next(1, 4);
                text.Append(digits).Append('0', zeros).Append(Exponent(exponent - zeros, random));
                break;
        }

        return text.ToString();
    }

    /// <summary>
    /// Splits a JSON number into its sign, its significant digits without leading or trailing
    /// zeros (empty for zero), and the exponent that goes with them.
    /// </summary>
    public static bool TryParse(string raw, out bool negative, out string digits, out int exponent)
    {
        negative = false;
        digits = string.Empty;
        exponent = 0;

        var i = 0;

        if (i < raw.Length && raw[i] == '-')
        {
            negative = true;
            i++;
        }

        var integerStart = i;

        if (i >= raw.Length || !char.IsAsciiDigit(raw[i]))
        {
            return false;
        }

        if (raw[i] == '0')
        {
            i++;
        }
        else
        {
            while (i < raw.Length && char.IsAsciiDigit(raw[i]))
            {
                i++;
            }
        }

        var all = new StringBuilder(raw[integerStart..i]);
        var fraction = 0;

        if (i < raw.Length && raw[i] == '.')
        {
            var fractionStart = ++i;

            while (i < raw.Length && char.IsAsciiDigit(raw[i]))
            {
                i++;
            }

            if (i == fractionStart)
            {
                return false;
            }

            all.Append(raw[fractionStart..i]);
            fraction = i - fractionStart;
        }

        var written = 0;

        if (i < raw.Length && (raw[i] == 'e' || raw[i] == 'E'))
        {
            i++;
            var sign = 1;

            if (i < raw.Length && (raw[i] == '+' || raw[i] == '-'))
            {
                sign = raw[i] == '-' ? -1 : 1;
                i++;
            }

            var exponentStart = i;

            while (i < raw.Length && char.IsAsciiDigit(raw[i]))
            {
                i++;
            }

            // Exponents this long are not what this respells.
            if (i == exponentStart || i - exponentStart > 6)
            {
                return false;
            }

            written = sign * int.Parse(raw.AsSpan(exponentStart, i - exponentStart), CultureInfo.InvariantCulture);
        }

        if (i != raw.Length)
        {
            return false;
        }

        var significant = all.ToString().TrimStart('0');
        exponent = written - fraction;

        if (significant.Length == 0)
        {
            return true;
        }

        var trimmed = significant.TrimEnd('0');
        exponent += significant.Length - trimmed.Length;
        digits = trimmed;
        return true;
    }

    /// <summary>The digits with the decimal point placed by the exponent: 0.5, 120, 0.0012, sometimes with zeros after.</summary>
    private static void Positional(StringBuilder text, string digits, int exponent, Random random)
    {
        if (exponent >= 0)
        {
            text.Append(digits).Append('0', exponent);

            if (random.Next(2) == 0)
            {
                text.Append('.').Append('0', random.Next(1, 4));
            }

            return;
        }

        var point = digits.Length + exponent;

        if (point > 0)
        {
            text.Append(digits.AsSpan(0, point)).Append('.').Append(digits.AsSpan(point));
        }
        else
        {
            text.Append("0.").Append('0', -point).Append(digits);
        }

        text.Append('0', random.Next(0, 3));
    }

    /// <summary>An exponent in one of the spellings JSON allows: e5, E+5, e05, e-05.</summary>
    private static string Exponent(int value, Random random)
    {
        var letter = random.Next(2) == 0 ? "e" : "E";
        var sign = value < 0 ? "-" : random.Next(2) == 0 ? "+" : string.Empty;
        var zeros = new string('0', random.Next(0, 3));

        return letter + sign + zeros + Math.Abs(value).ToString(CultureInfo.InvariantCulture);
    }
}
