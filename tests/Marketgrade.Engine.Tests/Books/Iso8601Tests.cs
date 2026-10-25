using System.Text;
using Marketgrade.Engine.Books;

namespace Marketgrade.Engine.Tests.Books;

public class Iso8601Tests
{
    [Theory]
    [InlineData("1970-01-01T00:00:00Z", 0)]
    [InlineData("1970-01-01T00:00:00.000000001Z", 1)]
    [InlineData("1970-01-01T00:00:01.5Z", 1_500_000_000)]
    // Coinbase: nanoseconds. Kraken: microseconds.
    [InlineData("2026-10-25T22:23:17.293696175Z", 1_792_966_997_293_696_175)]
    [InlineData("2026-10-25T22:23:18.098851Z", 1_792_966_998_098_851_000)]
    [InlineData("2024-02-29T12:00:00Z", 1_709_208_000_000_000_000)]
    [InlineData("1969-12-31T23:59:59Z", -1_000_000_000)]
    public void Parses_utc_timestamps(string text, long expected)
    {
        Assert.True(Iso8601.TryParseUnixNs(Encoding.ASCII.GetBytes(text), out var unixNs));
        Assert.Equal(expected, unixNs);
    }

    [Fact]
    public void Agrees_with_DateTime_parsing()
    {
        var text = "2026-10-25T22:23:17.2936961Z";
        var expected = (DateTime.Parse(text, null, System.Globalization.DateTimeStyles.RoundtripKind).Ticks - DateTime.UnixEpoch.Ticks) * 100;

        Assert.True(Iso8601.TryParseUnixNs(Encoding.ASCII.GetBytes(text), out var unixNs));
        Assert.Equal(expected, unixNs);
    }

    [Theory]
    [InlineData("")]
    [InlineData("2026-10-25")]
    [InlineData("2026-10-25T22:23:17")]
    [InlineData("2026-10-25T22:23:17.Z")]
    [InlineData("2026-10-25T22:23:17.1234567890Z")]
    [InlineData("2026-10-25T22:23:17+00:00")]
    [InlineData("2026-10-25T22:23:17.5+01:00")]
    [InlineData("2026-10-25 22:23:17Z")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-00-01T00:00:00Z")]
    [InlineData("2025-02-29T00:00:00Z")]
    [InlineData("2026-10-32T00:00:00Z")]
    [InlineData("2026-10-25T24:00:00Z")]
    [InlineData("2026-10-25T23:60:00Z")]
    [InlineData("2016-12-31T23:59:60Z")]
    [InlineData("0000-01-01T00:00:00Z")]
    [InlineData("2026-1a-25T22:23:17Z")]
    [InlineData("2026-10-25T22:23:17ZZ")]
    [InlineData("2026-10-25T22:23:17.5z")]
    public void Rejects_anything_else(string text)
    {
        Assert.False(Iso8601.TryParseUnixNs(Encoding.ASCII.GetBytes(text), out _));
    }
}
