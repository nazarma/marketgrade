using System.Text;

namespace Marketgrade.Engine.Books;

/// <summary>
/// Decimal text to and from a <see cref="long"/> scaled by 10^decimals, so <c>"80368.5"</c> with 2 decimals
/// is 8036850. Parsing and <see cref="TryFormat"/> never allocate.
/// </summary>
public static class ScaledDecimal
{
    public const int MaxDecimals = 18;

    /// <summary>
    /// Parses plain decimal text: digits, optionally a point and more digits. More decimals than
    /// <paramref name="decimals"/> are accepted only if the extra ones are zeros. Signs, exponents,
    /// a missing digit before or after the point and values that do not fit a long are rejected.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> text, int decimals, out long value)
    {
        value = 0;
        long result = 0;
        var i = 0;
        while (i < text.Length && IsDigit(text[i]))
        {
            if (!TryAppend(ref result, text[i] - '0'))
            {
                return false;
            }
            i++;
        }
        if (i == 0)
        {
            return false;
        }

        var used = 0;
        if (i < text.Length && text[i] == '.')
        {
            i++;
            var start = i;
            while (i < text.Length && IsDigit(text[i]))
            {
                var digit = text[i] - '0';
                if (used < decimals)
                {
                    if (!TryAppend(ref result, digit))
                    {
                        return false;
                    }
                    used++;
                }
                else if (digit != 0)
                {
                    return false;
                }
                i++;
            }
            if (i == start)
            {
                return false;
            }
        }
        if (i != text.Length)
        {
            return false;
        }

        for (; used < decimals; used++)
        {
            if (!TryAppend(ref result, 0))
            {
                return false;
            }
        }
        value = result;
        return true;
    }

    /// <summary>Writes <paramref name="value"/> with exactly <paramref name="decimals"/> decimals, for example <c>80368.50</c>.</summary>
    public static bool TryFormat(long value, int decimals, Span<byte> destination, out int written)
    {
        written = 0;
        Span<byte> digits = stackalloc byte[20];
        var magnitude = value < 0 ? 0 - (ulong)value : (ulong)value;
        var count = 0;
        do
        {
            digits[count++] = (byte)('0' + (int)(magnitude % 10));
            magnitude /= 10;
        }
        while (magnitude > 0);
        while (count <= decimals)
        {
            digits[count++] = (byte)'0';
        }

        var length = count + (value < 0 ? 1 : 0) + (decimals > 0 ? 1 : 0);
        if (destination.Length < length)
        {
            return false;
        }
        var position = 0;
        if (value < 0)
        {
            destination[position++] = (byte)'-';
        }
        for (var i = count - 1; i >= 0; i--)
        {
            destination[position++] = digits[i];
            if (i == decimals && decimals > 0)
            {
                destination[position++] = (byte)'.';
            }
        }
        written = position;
        return true;
    }

    public static string Format(long value, int decimals)
    {
        Span<byte> text = stackalloc byte[MaxDecimals + 22];
        TryFormat(value, decimals, text, out var written);
        return Encoding.ASCII.GetString(text[..written]);
    }

    private static bool IsDigit(byte c) => c is >= (byte)'0' and <= (byte)'9';

    private static bool TryAppend(ref long value, int digit)
    {
        if (value > (long.MaxValue - digit) / 10)
        {
            return false;
        }
        value = value * 10 + digit;
        return true;
    }
}
