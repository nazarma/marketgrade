using System.Text;
using Marketgrade.Engine.Books;

namespace Marketgrade.Engine.Tests.Books;

public class ScaledDecimalTests
{
    [Theory]
    [InlineData("0", 2, 0)]
    [InlineData("42", 2, 4200)]
    [InlineData("80368.5", 2, 8036850)]
    [InlineData("80368.50", 2, 8036850)]
    [InlineData("80368.50000000", 2, 8036850)]
    [InlineData("0.00042000", 5, 42)]
    [InlineData("0.00116000", 8, 116000)]
    [InlineData("007.5", 1, 75)]
    [InlineData("000", 0, 0)]
    [InlineData("12.000", 0, 12)]
    [InlineData("0.1", 18, 100_000_000_000_000_000)]
    [InlineData("92233720368.54775807", 8, long.MaxValue)]
    public void Parses_decimal_text(string text, int decimals, long expected)
    {
        Assert.True(ScaledDecimal.TryParse(Encoding.ASCII.GetBytes(text), decimals, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("", 2)]
    [InlineData(".", 2)]
    [InlineData(".5", 2)]
    [InlineData("5.", 2)]
    [InlineData("-1", 2)]
    [InlineData("+1", 2)]
    [InlineData("1e-8", 8)]
    [InlineData("1E5", 2)]
    [InlineData("1.5e2", 2)]
    [InlineData("1.2.3", 2)]
    [InlineData(" 1", 2)]
    [InlineData("1 ", 2)]
    [InlineData("0x10", 2)]
    [InlineData("NaN", 2)]
    [InlineData("0.001", 2)]
    [InlineData("80368.501", 2)]
    [InlineData("0.000420001", 5)]
    [InlineData("92233720368.54775808", 8)]
    [InlineData("9223372036854775808", 0)]
    [InlineData("100000000000", 8)]
    [InlineData("99999999999999999999999", 0)]
    public void Rejects_invalid_or_too_precise_text(string text, int decimals)
    {
        Assert.False(ScaledDecimal.TryParse(Encoding.ASCII.GetBytes(text), decimals, out _));
    }

    [Theory]
    [InlineData(8036850, 2, "80368.50")]
    [InlineData(116000, 8, "0.00116000")]
    [InlineData(5, 2, "0.05")]
    [InlineData(0, 2, "0.00")]
    [InlineData(0, 0, "0")]
    [InlineData(1234, 0, "1234")]
    [InlineData(-150, 2, "-1.50")]
    [InlineData(long.MaxValue, 8, "92233720368.54775807")]
    [InlineData(long.MinValue, 0, "-9223372036854775808")]
    public void Formats_with_exactly_the_pair_decimals(long value, int decimals, string expected)
    {
        Assert.Equal(expected, ScaledDecimal.Format(value, decimals));
    }

    [Fact]
    public void Format_reports_a_destination_that_is_too_small()
    {
        Assert.False(ScaledDecimal.TryFormat(8036850, 2, new byte[7], out _));
        Assert.True(ScaledDecimal.TryFormat(8036850, 2, new byte[8], out var written));
        Assert.Equal(8, written);
    }

    [Theory]
    [InlineData("0.01000000", 2)]
    [InlineData("0.00001000", 5)]
    [InlineData("0.00000001", 8)]
    [InlineData("0.1", 1)]
    [InlineData("0.5", 1)]
    [InlineData("1", 0)]
    [InlineData("1.0", 0)]
    [InlineData("10", 0)]
    public void Derives_decimals_from_a_tick_size(string tick, int expected)
    {
        Assert.Equal(expected, PairRules.DecimalsFrom(tick));
    }

    [Theory]
    [InlineData("")]
    [InlineData("1e-8")]
    [InlineData("-0.01")]
    [InlineData(".01")]
    [InlineData("0.0000000000000000001")]
    public void Rejects_an_invalid_tick_size(string tick)
    {
        Assert.Throws<FormatException>(() => PairRules.DecimalsFrom(tick));
    }
}
