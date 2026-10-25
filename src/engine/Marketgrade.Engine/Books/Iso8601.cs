namespace Marketgrade.Engine.Books;

/// <summary>Exchange timestamps such as <c>2026-10-25T22:23:17.293696175Z</c> to Unix nanoseconds, without allocating.</summary>
public static class Iso8601
{
    /// <summary>
    /// Parses <c>yyyy-MM-ddTHH:mm:ss</c>, an optional fraction of 1 to 9 digits, and <c>Z</c>. Other offsets,
    /// leap seconds and anything else are rejected.
    /// </summary>
    public static bool TryParseUnixNs(ReadOnlySpan<byte> text, out long unixNs)
    {
        unixNs = 0;
        if (text.Length < 20 || text[4] != '-' || text[7] != '-' || text[10] != 'T' || text[13] != ':' || text[16] != ':'
            || !TryDigits(text[..4], out var year)
            || !TryDigits(text.Slice(5, 2), out var month)
            || !TryDigits(text.Slice(8, 2), out var day)
            || !TryDigits(text.Slice(11, 2), out var hour)
            || !TryDigits(text.Slice(14, 2), out var minute)
            || !TryDigits(text.Slice(17, 2), out var second))
        {
            return false;
        }
        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        long fractionNs = 0;
        var i = 19;
        if (text[i] == '.')
        {
            i++;
            var start = i;
            while (i < text.Length && text[i] is >= (byte)'0' and <= (byte)'9')
            {
                if (i - start == 9)
                {
                    return false;
                }
                fractionNs = fractionNs * 10 + (text[i] - '0');
                i++;
            }
            var digits = i - start;
            if (digits == 0)
            {
                return false;
            }
            for (; digits < 9; digits++)
            {
                fractionNs *= 10;
            }
        }
        if (i != text.Length - 1 || text[i] != 'Z')
        {
            return false;
        }

        var ticks = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks;
        unixNs = ticks * 100 + fractionNs;
        return true;
    }

    private static bool TryDigits(ReadOnlySpan<byte> text, out int value)
    {
        value = 0;
        foreach (var c in text)
        {
            if (c is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
            value = value * 10 + (c - '0');
        }
        return true;
    }
}
