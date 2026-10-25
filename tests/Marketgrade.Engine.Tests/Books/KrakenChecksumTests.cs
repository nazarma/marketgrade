using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Marketgrade.Engine.Books;

namespace Marketgrade.Engine.Tests.Books;

public class KrakenChecksumTests
{
    private static readonly PairRules s_rules = new(1, "BTC/USD", PriceDecimals: 1, AmountDecimals: 8);

    [Fact]
    public void Documented_example_gives_3310070434()
    {
        // https://docs.kraken.com/api/docs/guides/spot-ws-book-v2, "Example".
        var book = Book(
            asks:
            [
                ("45285.2", "0.00100000"), ("45286.4", "1.54571953"), ("45286.6", "1.54571109"), ("45289.6", "1.54560911"),
                ("45290.2", "0.15890660"), ("45291.8", "1.54553491"), ("45294.7", "0.04454749"), ("45296.1", "0.35380000"),
                ("45297.5", "0.09945542"), ("45299.5", "0.18772827"),
            ],
            bids:
            [
                ("45283.5", "0.10000000"), ("45283.4", "1.54582015"), ("45282.1", "0.10000000"), ("45281.0", "0.10000000"),
                ("45280.3", "1.54592586"), ("45279.0", "0.07990000"), ("45277.6", "0.03310103"), ("45277.5", "0.30000000"),
                ("45277.3", "1.54602737"), ("45276.6", "0.15445238"),
            ]);

        Assert.Equal(3310070434u, KrakenChecksum.Compute(book));
    }

    [Fact]
    public void Kraken_sends_numbers_without_trailing_zeros_and_the_checksum_is_unchanged()
    {
        var full = Book(asks: [("45285.2", "0.00100000")], bids: [("45283.5", "0.10000000")]);
        var trimmed = Book(asks: [("45285.2", "0.001")], bids: [("45283.5", "0.1")]);

        Assert.Equal(KrakenChecksum.Compute(full), KrakenChecksum.Compute(trimmed));
    }

    [Fact]
    public void Sequence_of_updates_with_truncation_matches_the_documented_method()
    {
        const int depth = 25;
        var random = new Random(7);
        var book = new OrderBook(s_rules, capacity: 64);
        for (var i = 0; i < 2_000; i++)
        {
            var bids = random.Next(2) == 0;
            // Around a mid price of 45284.0, with some levels far out of range.
            var offset = random.Next(1, 400);
            var price = bids ? 452840 - offset : 452840 + offset;
            var amount = random.Next(4) == 0 ? 0 : random.Next(1, 200_000_000);
            (bids ? book.Bids : book.Asks).Set(price, amount);
            book.Bids.Truncate(depth);
            book.Asks.Truncate(depth);

            Assert.Equal(Reference(book), KrakenChecksum.Compute(book));
        }
        Assert.Equal(depth, book.Bids.Count);
        Assert.Equal(depth, book.Asks.Count);
    }

    /// <summary>The method as Kraken documents it, on decimal strings.</summary>
    private static uint Reference(OrderBook book)
    {
        var text = new StringBuilder();
        foreach (var side in new[] { book.Asks, book.Bids })
        {
            for (var i = 0; i < Math.Min(10, side.Count); i++)
            {
                text.Append(Strip((side.PriceAt(i) / 10m).ToString("F1", CultureInfo.InvariantCulture)));
                text.Append(Strip((side.AmountAt(i) / 100_000_000m).ToString("F8", CultureInfo.InvariantCulture)));
            }
        }
        return Crc32.HashToUInt32(Encoding.ASCII.GetBytes(text.ToString()));

        static string Strip(string number) => number.Replace(".", "", StringComparison.Ordinal).TrimStart('0');
    }

    private static OrderBook Book((string Price, string Qty)[] asks, (string Price, string Qty)[] bids)
    {
        var book = new OrderBook(s_rules, capacity: 16);
        foreach (var (side, levels) in new[] { (book.Asks, asks), (book.Bids, bids) })
        {
            foreach (var (price, qty) in levels)
            {
                Assert.True(ScaledDecimal.TryParse(Encoding.ASCII.GetBytes(price), s_rules.PriceDecimals, out var p));
                Assert.True(ScaledDecimal.TryParse(Encoding.ASCII.GetBytes(qty), s_rules.AmountDecimals, out var q));
                side.Set(p, q);
            }
        }
        return book;
    }
}
