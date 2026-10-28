using Marketgrade.Engine.Books;
using Marketgrade.Engine.Flow;

namespace Marketgrade.Backend.Tests;

public class FlowSamplerTests
{
    private static readonly DateTime s_t0 = new(2026, 10, 28, 22, 30, 0, DateTimeKind.Utc);
    private static readonly PairRules s_rules = new(2, "BTC-USD", 2, 8);

    private sealed class FakeFlow : IExchangeFlow
    {
        public int ExchangeId => 2;
        public string Exchange => "Coinbase";
        public string Pair => "BTC-USD";
        public PairRules? Rules { get; set; } = s_rules;
        public FlowRing Ring { get; } = new(1024);
        public long BookChanges { get; set; }
        public long Trades { get; set; }
        public BookSnapshot? Book { get; set; }

        public BookSnapshot? ReadBook() => Book;

        public void Change(long time, FlowSide side, long price, long amount) =>
            Ring.Write(new FlowEvent(time, FlowKind.BookChange, side, price, amount));

        public void Trade(long time, FlowSide side, long price, long amount) =>
            Ring.Write(new FlowEvent(time, FlowKind.Trade, side, price, amount));
    }

    private static BookSnapshot Book(BookState state, int levels) => new("Coinbase", "BTC-USD", state, s_t0, s_rules,
        [.. Enumerable.Range(0, levels).Select(i => 8_100_000L - i)], [.. Enumerable.Repeat(100_000_000L, levels)],
        [.. Enumerable.Range(0, levels).Select(i => 8_100_002L + i)], [.. Enumerable.Repeat(50_000_000L, levels)]);

    [Fact]
    public void Starts_with_events_written_after_it_was_created()
    {
        var flow = new FakeFlow();
        flow.Trade(1_000_000, FlowSide.Buy, 8_100_001, 1);
        flow.Trades = 1;
        var sampler = new FlowSampler([flow], s_t0);

        flow.Trade(2_000_000, FlowSide.Sell, 8_100_000, 2);
        flow.Trades = 2;
        var frame = sampler.Sample(s_t0.AddMilliseconds(100));

        var exchange = Assert.Single(frame.Exchanges);
        Assert.Equal(1, exchange.Trades);
        Assert.Equal(new FlowItem(2, FlowKind.Trade, FlowSide.Sell, 81_000.00, 0.00000002), Assert.Single(exchange.Events));
        Assert.Equal(0.1, frame.IntervalSeconds, 6);
    }

    [Fact]
    public void Keeps_every_trade_up_to_50_and_the_40_largest_book_changes_in_time_order()
    {
        var flow = new FakeFlow();
        var sampler = new FlowSampler([flow], s_t0);
        long time = 0;
        for (var i = 0; i < 60; i++)
        {
            flow.Trade(++time * 1_000_000, FlowSide.Buy, 8_100_001, i + 1);
        }
        for (var i = 0; i < 100; i++)
        {
            // Amounts 1 to 100, removals among them; the largest |change| wins.
            flow.Change(++time * 1_000_000, i % 2 == 0 ? FlowSide.Buy : FlowSide.Sell, 8_100_000, i % 3 == 0 ? -(i + 1) : i + 1);
        }
        flow.Trades = 60;
        flow.BookChanges = 5_000;

        var exchange = sampler.Sample(s_t0.AddMilliseconds(100)).Exchanges[0];

        var trades = exchange.Events.Where(e => e.Kind == FlowKind.Trade).ToList();
        Assert.Equal(FlowSampler.MaxTrades, trades.Count);
        Assert.Equal(11, trades[0].Amount * 1e8, 6);
        var changes = exchange.Events.Where(e => e.Kind == FlowKind.BookChange).ToList();
        Assert.Equal(FlowSampler.MaxBookChanges, changes.Count);
        Assert.All(changes, c => Assert.True(Math.Abs(c.Amount * 1e8) > 60.5));
        Assert.Contains(changes, c => c.Amount < 0);
        Assert.Equal(exchange.Events.OrderBy(e => e.TimeMs), exchange.Events);
        Assert.Equal((5_000L, 60L, 0L), (exchange.BookChanges, exchange.Trades, exchange.Lost));
    }

    [Fact]
    public void Counts_are_per_interval_and_overwritten_events_are_reported()
    {
        var flow = new FakeFlow { BookChanges = 1_000, Trades = 10 };
        var sampler = new FlowSampler([flow], s_t0);
        for (var i = 0; i < 1_500; i++)
        {
            flow.Change(i, FlowSide.Buy, 8_100_000, 1);
        }
        flow.BookChanges = 2_500;
        flow.Trades = 13;

        var exchange = sampler.Sample(s_t0.AddMilliseconds(100)).Exchanges[0];

        Assert.Equal((1_500L, 3L), (exchange.BookChanges, exchange.Trades));
        Assert.Equal(1_500 - 1_023, exchange.Lost);
    }

    [Fact]
    public void A_valid_book_gives_the_mid_and_12_levels_per_side()
    {
        var flow = new FakeFlow { Book = Book(BookState.Valid, 20) };
        var sampler = new FlowSampler([flow], s_t0);

        var exchange = sampler.Sample(s_t0.AddMilliseconds(100)).Exchanges[0];

        Assert.Equal(81_000.01, exchange.Mid);
        Assert.Equal(FlowSampler.DepthLevels, exchange.Bids.Length);
        Assert.Equal([81_000.00, 1.0], exchange.Bids[0]);
        Assert.Equal([81_000.02, 0.5], exchange.Asks[0]);
        Assert.Equal(2, exchange.PriceDecimals);
    }

    [Fact]
    public void Without_a_valid_book_or_rules_there_is_no_mid_or_depth()
    {
        var invalid = new FakeFlow { Book = Book(BookState.Invalid, 20) };
        var noRules = new FakeFlow { Rules = null, Book = Book(BookState.Valid, 20) };
        noRules.Trade(1, FlowSide.Buy, 1, 1);
        var sampler = new FlowSampler([invalid, noRules], s_t0);
        noRules.Trade(2, FlowSide.Buy, 1, 1);

        var frame = sampler.Sample(s_t0.AddMilliseconds(100));

        Assert.Null(frame.Exchanges[0].Mid);
        Assert.Empty(frame.Exchanges[0].Bids);
        Assert.Null(frame.Exchanges[1].Mid);
        Assert.Empty(frame.Exchanges[1].Events);
    }
}
