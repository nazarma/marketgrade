using System.Buffers.Text;
using System.IO.Hashing;

namespace Marketgrade.Engine.Books;

/// <summary>
/// Kraken's book checksum: CRC32 of the top 10 asks (lowest first), then the top 10 bids (highest first),
/// each written as price then quantity at the pair's full precision without the decimal point and
/// leading zeros. For a scaled long that text is just its decimal digits, so no string is ever built.
/// </summary>
public static class KrakenChecksum
{
    public const int Levels = 10;

    // 20 levels, two numbers each, at most 19 digits per long.
    private const int MaxLength = 2 * Levels * 2 * 19;

    public static uint Compute(OrderBook book)
    {
        Span<byte> text = stackalloc byte[MaxLength];
        var length = Append(book.Asks, text, 0);
        length = Append(book.Bids, text, length);
        return Crc32.HashToUInt32(text[..length]);
    }

    private static int Append(OrderBookSide side, Span<byte> text, int length)
    {
        var levels = Math.Min(Levels, side.Count);
        for (var i = 0; i < levels; i++)
        {
            Utf8Formatter.TryFormat(side.PriceAt(i), text[length..], out var written);
            length += written;
            Utf8Formatter.TryFormat(side.AmountAt(i), text[length..], out written);
            length += written;
        }
        return length;
    }
}
