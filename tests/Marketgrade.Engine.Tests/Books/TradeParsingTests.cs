using System.Text.RegularExpressions;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Flow;

namespace Marketgrade.Engine.Tests.Books;

/// <summary>Trades from captured messages, into the flow ring; they never touch the book or its check.</summary>
public partial class TradeParsingTests
{
    private readonly MessageBufferPool _pool = new(4, 64 * 1024);
    private readonly FlowRing _flow = new(64);

    private static string Fixture(string path) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", path));

    private static long UnixNs(string iso) => (DateTime.Parse(iso, null, System.Globalization.DateTimeStyles.AdjustToUniversal).Ticks - DateTime.UnixEpoch.Ticks) * 100;

    private BookHandler Create(ExchangeAdapter adapter, int priceDecimals, int amountDecimals)
    {
        var handler = adapter.CreateBookHandler(new PairRules(adapter.Id, adapter.Pair, priceDecimals, amountDecimals), new EngineOptions())!;
        handler.Flow = _flow;
        return handler;
    }

    private HandleOutcome Handle(BookHandler handler, string message, long connectionId = 1)
    {
        var buffer = TestMessages.Buffer(_pool, message, connectionId: connectionId);
        var outcome = handler.Handle(buffer);
        buffer.Release();
        return outcome;
    }

    private FlowEvent[] Flow()
    {
        var events = new FlowEvent[_flow.Capacity];
        return events[.._flow.Read(0, events).Count];
    }

    [Fact]
    public void Kraken_trade_side_is_the_taker()
    {
        var handler = Create(new KrakenAdapter(), 1, 8);

        Assert.Equal(HandleOutcome.Ignored, Handle(handler, Fixture("kraken/trade-update.json")));

        Assert.Equal(MessageKind.Trade, handler.Kind);
        Assert.Equal(1, handler.Trades);
        Assert.Equal([new FlowEvent(UnixNs("2026-10-28T22:30:28.365025Z"), FlowKind.Trade, FlowSide.Sell, 811_954, 2_832)], Flow());
        Assert.Equal(BookState.Syncing, handler.Book.State);
        Assert.Equal(0, handler.ExchangeUnixNs);
    }

    [Fact]
    public void Kraken_trade_snapshot_is_history_and_skipped()
    {
        var handler = Create(new KrakenAdapter(), 1, 8);

        Handle(handler, Fixture("kraken/trade-update.json").Replace("\"update\"", "\"snapshot\"", StringComparison.Ordinal));

        Assert.Equal(0, handler.Trades);
        Assert.Empty(Flow());
    }

    [Fact]
    public void Coinbase_trade_side_is_the_maker_so_the_taker_is_the_other_side()
    {
        var handler = Create(new CoinbaseAdapter(), 2, 8);

        Handle(handler, Fixture("coinbase/trade-update.json"));

        // "side":"SELL": the maker sold, so the taker bought.
        Assert.Equal([new FlowEvent(UnixNs("2026-10-28T22:30:28.758187Z"), FlowKind.Trade, FlowSide.Buy, 8_117_677, 57_970)], Flow());
    }

    [Fact]
    public void Coinbase_trade_snapshot_is_history_and_skipped()
    {
        var handler = Create(new CoinbaseAdapter(), 2, 8);

        Assert.Equal(HandleOutcome.Ignored, Handle(handler, Fixture("coinbase/trade-snapshot.json")));

        Assert.Equal(0, handler.Trades);
        Assert.Empty(Flow());
    }

    [Fact]
    public void Coinbase_trade_messages_are_part_of_the_connection_sequence()
    {
        var handler = Create(new CoinbaseAdapter(), 2, 8);
        var recorded = TestMessages.Sequence("coinbase-after-snapshot.jsonl");
        var heartbeat = Array.FindIndex(recorded, m => m.StartsWith("{\"channel\":\"heartbeats\"", StringComparison.Ordinal));
        // The heartbeat's place in the sequence taken by a trade message.
        recorded[heartbeat] = SequenceNum().Replace(Fixture("coinbase/trade-update.json"), $"\"sequence_num\":{heartbeat + 1}");

        Assert.Equal(HandleOutcome.Synced, Handle(handler, Fixture("coinbase/data-l2-snapshot-trimmed.json")));
        Assert.All(recorded, m => Assert.NotEqual(HandleOutcome.Failed, Handle(handler, m)));
        Assert.Equal(BookState.Valid, handler.Book.State);
        Assert.Single(Flow());

        // Leaving one out is a gap like any other message.
        var missing = Create(new CoinbaseAdapter(), 2, 8);
        Handle(missing, Fixture("coinbase/data-l2-snapshot-trimmed.json"));
        for (var i = 0; i < heartbeat; i++)
        {
            Handle(missing, recorded[i]);
        }
        Assert.Equal(HandleOutcome.Failed, Handle(missing, recorded[heartbeat + 1]));
        Assert.Equal(FailureKind.Sequence, missing.Failure);
    }

    [Fact]
    public void Unreadable_trade_is_counted_and_leaves_the_book_valid()
    {
        var handler = Create(new CoinbaseAdapter(), 2, 8);
        var recorded = TestMessages.Sequence("coinbase-after-snapshot.jsonl");
        Handle(handler, Fixture("coinbase/data-l2-snapshot-trimmed.json"));
        Handle(handler, recorded[0]);
        var broken = SequenceNum().Replace(Fixture("coinbase/trade-update.json"), "\"sequence_num\":2")
            .Replace("\"81176.77\"", "\"81176.777\"", StringComparison.Ordinal);

        Assert.Equal(HandleOutcome.Ignored, Handle(handler, broken));

        Assert.Equal(1, handler.TradeFailures);
        Assert.Equal(BookState.Valid, handler.Book.State);
        Assert.Empty(Flow());
        // The trade took number 2, so the sequence goes on with number 3.
        Assert.All(recorded[2..], m => Assert.NotEqual(HandleOutcome.Failed, Handle(handler, m)));
        Assert.Equal(BookState.Valid, handler.Book.State);
    }

    [Fact]
    public void Coinbase_trade_unreadable_before_its_sequence_number_fails_the_book()
    {
        var handler = Create(new CoinbaseAdapter(), 2, 8);
        Handle(handler, Fixture("coinbase/data-l2-snapshot-trimmed.json"));
        const string message = """{"channel":"market_trades","events":[{"type":"update","trades":[{"price":"x"}]}],"sequence_num":1}""";

        Assert.Equal(HandleOutcome.Failed, Handle(handler, message));

        Assert.Equal(FailureKind.Parse, handler.Failure);
    }

    [Fact]
    public void Kraken_unreadable_trade_is_only_counted()
    {
        var handler = Create(new KrakenAdapter(), 1, 8);
        var snapshot = TestMessages.Sequence("kraken-book.jsonl").First(l => l.Contains("\"type\":\"snapshot\"", StringComparison.Ordinal));
        Assert.Equal(HandleOutcome.Synced, Handle(handler, snapshot));

        Assert.Equal(HandleOutcome.Ignored, Handle(handler, Fixture("kraken/trade-update.json").Replace("\"sell\"", "\"up\"", StringComparison.Ordinal)));

        Assert.Equal(1, handler.TradeFailures);
        Assert.Equal(BookState.Valid, handler.Book.State);
    }

    [Theory]
    [InlineData("binance/trade-taker-buy.json", FlowSide.Buy, 8_127_466, 44)]
    [InlineData("binance/trade-taker-sell.json", FlowSide.Sell, 8_127_465, 693)]
    public void Binance_buyer_as_maker_means_the_taker_sold(string fixture, FlowSide taker, long price, long amount)
    {
        var handler = Create(new BinanceAdapter(), 2, 5);

        Assert.Equal(HandleOutcome.Ignored, Handle(handler, Fixture(fixture)));

        var trade = Assert.Single(Flow());
        Assert.Equal(new FlowEvent(trade.TimeNs, FlowKind.Trade, taker, price, amount), trade);
        Assert.InRange(trade.TimeNs, 1_793_226_629_639_000_000, 1_793_226_629_640_000_000);
        Assert.Equal(0, handler.ExchangeUnixNs);
    }

    [Fact]
    public void Without_a_flow_ring_trades_are_still_counted()
    {
        var handler = Create(new BinanceAdapter(), 2, 5);
        handler.Flow = null;

        Handle(handler, Fixture("binance/trade-taker-buy.json"));

        Assert.Equal(1, handler.Trades);
        Assert.Equal(0, _flow.Published);
    }

    [GeneratedRegex("\"sequence_num\":\\d+")]
    private static partial Regex SequenceNum();
}
