namespace Kkdev92.Jev.Serialization;

/// <summary>
/// Reads a JSON number as an exact 64-bit integer, whatever notation it arrives in.
/// </summary>
/// <remarks>
/// <para>
/// The contract types token counts as <c>integer</c>, and JSON Schema's <c>integer</c> is any number
/// with no fractional part: <c>120</c>, <c>120.0</c> and <c>1.2e2</c> are all the same value. A
/// reader that only accepts the first form rejects a correct response; a reader that goes through
/// <see cref="double"/> silently rounds anything past 2^53. This does neither. It works on the
/// digits of the token itself, and says no — rather than rounding — to a fraction or a value that
/// does not fit.
/// </para>
/// <para>
/// The exponent is read with saturation, so <c>1e99999999999999999999</c> is refused as too large
/// rather than overflowing the parser, and <c>0e99999999999999999999</c> is still zero.
/// </para>
/// </remarks>
internal static class JsonExactInteger
{
    /// <summary>Beyond this the exponent no longer matters: the value is zero, a fraction, or too large.</summary>
    private const long ExponentSaturation = 1_000_000_000_000_000;

    /// <summary>The number of decimal digits in <see cref="long.MaxValue"/>.</summary>
    private const int MaxInt64Digits = 19;

    /// <summary>
    /// Parses a JSON number token. Returns false for anything that is not a syntactically valid JSON
    /// number, has a fractional part, or does not fit in a <see cref="long"/>.
    /// </summary>
    public static bool TryParseInt64(ReadOnlySpan<byte> token, out long value)
    {
        value = 0;

        if (!TrySplit(token, out var negative, out var integerDigits, out var fractionDigits, out var exponent))
        {
            return false;
        }

        // The significant digits run across the decimal point: value = digits × 10^scale, where the
        // digits are the integer part followed by the fraction part.
        var digits = new DigitRun(integerDigits, fractionDigits);
        var start = 0;

        while (start < digits.Length && digits[start] == '0')
        {
            start++;
        }

        if (start == digits.Length)
        {
            // Zero in any spelling, including -0 and 0e999: an integer.
            return true;
        }

        var end = digits.Length;
        var scale = exponent - fractionDigits.Length;

        while (digits[end - 1] == '0')
        {
            end--;
            scale++;
        }

        if (scale < 0 || end - start + scale > MaxInt64Digits)
        {
            // Either the last significant digit sits right of the decimal point, or there are more
            // digits than a long can hold.
            return false;
        }

        ulong magnitude = 0;

        for (var d = start; d < end; d++)
        {
            magnitude = (magnitude * 10) + (ulong)(digits[d] - '0');
        }

        for (var s = 0L; s < scale; s++)
        {
            magnitude *= 10;
        }

        // Nineteen digits fit in a ulong without overflow (its maximum has twenty), so the only
        // question left is whether the value fits the signed range.
        if (negative)
        {
            if (magnitude > (ulong)long.MaxValue + 1)
            {
                return false;
            }

            value = unchecked(-(long)magnitude);
            return true;
        }

        if (magnitude > long.MaxValue)
        {
            return false;
        }

        value = (long)magnitude;
        return true;
    }

    /// <summary>Splits a JSON number into sign, integer digits, fraction digits and a saturated exponent.</summary>
    private static bool TrySplit(
        ReadOnlySpan<byte> token,
        out bool negative,
        out ReadOnlySpan<byte> integerDigits,
        out ReadOnlySpan<byte> fractionDigits,
        out long exponent)
    {
        var i = 0;
        negative = false;
        integerDigits = default;
        fractionDigits = default;
        exponent = 0;

        if (i < token.Length && token[i] == (byte)'-')
        {
            negative = true;
            i++;
        }

        // The integer part is '0' alone, or a non-zero digit followed by digits.
        var integerStart = i;

        if (i >= token.Length || !IsDigit(token[i]))
        {
            return false;
        }

        if (token[i] == (byte)'0')
        {
            i++;
        }
        else
        {
            while (i < token.Length && IsDigit(token[i]))
            {
                i++;
            }
        }

        integerDigits = token[integerStart..i];

        if (i < token.Length && token[i] == (byte)'.')
        {
            var fractionStart = ++i;

            while (i < token.Length && IsDigit(token[i]))
            {
                i++;
            }

            if (i == fractionStart)
            {
                return false;
            }

            fractionDigits = token[fractionStart..i];
        }

        if (i < token.Length && (token[i] == (byte)'e' || token[i] == (byte)'E'))
        {
            i++;
            var exponentNegative = false;

            if (i < token.Length && (token[i] == (byte)'+' || token[i] == (byte)'-'))
            {
                exponentNegative = token[i] == (byte)'-';
                i++;
            }

            var exponentStart = i;

            while (i < token.Length && IsDigit(token[i]))
            {
                if (exponent < ExponentSaturation)
                {
                    exponent = (exponent * 10) + (token[i] - '0');
                }

                i++;
            }

            if (i == exponentStart)
            {
                return false;
            }

            if (exponentNegative)
            {
                exponent = -exponent;
            }
        }

        return i == token.Length;
    }

    private static bool IsDigit(byte b) => (uint)(b - '0') <= 9;

    /// <summary>The integer and fraction digits read as one run, without copying either.</summary>
    private readonly ref struct DigitRun(ReadOnlySpan<byte> integer, ReadOnlySpan<byte> fraction)
    {
        private readonly ReadOnlySpan<byte> _integer = integer;
        private readonly ReadOnlySpan<byte> _fraction = fraction;

        public int Length => _integer.Length + _fraction.Length;

        public byte this[int index] => index < _integer.Length ? _integer[index] : _fraction[index - _integer.Length];
    }
}
