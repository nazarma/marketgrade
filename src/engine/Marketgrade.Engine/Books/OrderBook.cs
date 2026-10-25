using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Books;

public enum BookState
{
    /// <summary>The pair's decimals could not be fetched or loaded from the cache, so there is no book.</summary>
    NoPairRules,

    /// <summary>Waiting for a snapshot.</summary>
    Syncing,

    Valid,

    /// <summary>A check failed or the connection was lost; a repair is under way.</summary>
    Invalid,
}

/// <summary>The live book of one pair, owned by a single thread. Prices and amounts are scaled by the pair's decimals.</summary>
public sealed class OrderBook(PairRules rules, int capacity, string exchange = "", ILogger? logger = null)
{
    public PairRules Rules { get; } = rules;

    public OrderBookSide Bids { get; } = new(bids: true, capacity, exchange, logger);

    public OrderBookSide Asks { get; } = new(bids: false, capacity, exchange, logger);

    public BookState State { get; set; } = BookState.Syncing;

    /// <summary>Arrival time of the last message applied, Unix nanoseconds.</summary>
    public long LastArrivalUnixNs { get; set; }

    /// <summary>The exchange's timestamp of the last message applied, Unix nanoseconds, or 0.</summary>
    public long LastExchangeUnixNs { get; set; }

    public void Clear()
    {
        Bids.Clear();
        Asks.Clear();
    }
}
