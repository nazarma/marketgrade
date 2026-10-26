namespace Marketgrade.Engine.Books;

/// <summary>
/// An immutable copy of the best levels of a book. The book worker publishes a new one at most 10 times
/// per second, and only while someone reads them; readers never lock.
/// </summary>
/// <param name="BidPrices">Best first, scaled by <see cref="PairRules.PriceDecimals"/>; amounts by <see cref="PairRules.AmountDecimals"/>.</param>
public sealed record BookSnapshot(
    string Exchange,
    string Pair,
    BookState State,
    DateTime TimeUtc,
    PairRules Rules,
    long[] BidPrices,
    long[] BidAmounts,
    long[] AskPrices,
    long[] AskAmounts)
{
    public const int MaxLevels = 100;

    internal static BookSnapshot From(OrderBook book, string exchange, string pair, DateTime timeUtc)
    {
        var bids = Math.Min(MaxLevels, book.Bids.Count);
        var asks = Math.Min(MaxLevels, book.Asks.Count);
        var snapshot = new BookSnapshot(exchange, pair, book.State, timeUtc, book.Rules,
            new long[bids], new long[bids], new long[asks], new long[asks]);
        for (var i = 0; i < bids; i++)
        {
            snapshot.BidPrices[i] = book.Bids.PriceAt(i);
            snapshot.BidAmounts[i] = book.Bids.AmountAt(i);
        }
        for (var i = 0; i < asks; i++)
        {
            snapshot.AskPrices[i] = book.Asks.PriceAt(i);
            snapshot.AskAmounts[i] = book.Asks.AmountAt(i);
        }
        return snapshot;
    }
}
