using Marketgrade.Engine.Books;

namespace Marketgrade.Backend;

/// <summary>The best levels of a book as <c>GET /books/{exchange}</c> returns them: <c>[price, amount]</c> strings with the pair's decimals.</summary>
public sealed record BookResponse(string Exchange, string Pair, BookState State, DateTime TimeUtc, string[][] Bids, string[][] Asks)
{
    public const int DefaultLevels = 20;

    public static BookResponse From(BookSnapshot book, int levels) => new(
        book.Exchange,
        book.Pair,
        book.State,
        book.TimeUtc,
        Levels(book.BidPrices, book.BidAmounts, book.Rules, levels),
        Levels(book.AskPrices, book.AskAmounts, book.Rules, levels));

    private static string[][] Levels(long[] prices, long[] amounts, PairRules rules, int levels) =>
        [.. Enumerable.Range(0, Math.Min(levels, prices.Length)).Select(i => new[]
        {
            ScaledDecimal.Format(prices[i], rules.PriceDecimals),
            ScaledDecimal.Format(amounts[i], rules.AmountDecimals),
        })];
}
