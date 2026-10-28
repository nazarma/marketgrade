using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Flow;

namespace Marketgrade.Engine.Tests.Books;

/// <summary>Book changes into the flow ring: only updates to a valid book, only near the mid price, with the signed change.</summary>
public class FlowBookChangeTests
{
    private const long Arrival = 1_793_226_629_000_000_000;

    private readonly MessageBufferPool _pool = new(4, 64 * 1024);
    private readonly FlowRing _flow = new(64);
    private readonly BookHandler _handler =
        new CoinbaseAdapter().CreateBookHandler(new PairRules(2, "BTC-USD", 2, 8), new EngineOptions())!;
    private long _sequence;

    public FlowBookChangeTests() => _handler.Flow = _flow;

    /// <summary>Ten levels per side around 81,000.00, a cent apart, 1 BTC each.</summary>
    private static string Snapshot
    {
        get
        {
            var levels = Enumerable.Range(0, 10).SelectMany(i => new[]
            {
                $$"""{"side":"bid","event_time":"2026-10-28T22:30:27.000000Z","price_level":"{{ScaledDecimal.Format(8_099_999 - i, 2)}}","new_quantity":"1"}""",
                $$"""{"side":"offer","event_time":"2026-10-28T22:30:27.000000Z","price_level":"{{ScaledDecimal.Format(8_100_001 + i, 2)}}","new_quantity":"1"}""",
            });
            return $$"""{"channel":"l2_data","timestamp":"2026-10-28T22:30:27.100000Z","sequence_num":0,"events":[{"type":"snapshot","product_id":"BTC-USD","updates":[{{string.Join(",", levels)}}]}]}""";
        }
    }

    private HandleOutcome Handle(string message)
    {
        var buffer = TestMessages.Buffer(_pool, message, arrivalUnixNs: Arrival);
        var outcome = _handler.Handle(buffer);
        buffer.Release();
        return outcome;
    }

    /// <summary>One l2_data update with the next sequence number; prices and amounts scaled by 2 and 8 decimals.</summary>
    private HandleOutcome Update(params (string Side, long Price, long Amount)[] levels)
    {
        var updates = string.Join(",", levels.Select(l =>
            $$"""{"side":"{{l.Side}}","event_time":"2026-10-28T22:30:28.000000Z","price_level":"{{ScaledDecimal.Format(l.Price, 2)}}","new_quantity":"{{ScaledDecimal.Format(l.Amount, 8)}}"}"""));
        return Handle($$"""{"channel":"l2_data","timestamp":"2026-10-28T22:30:28.100000Z","sequence_num":{{++_sequence}},"events":[{"type":"update","product_id":"BTC-USD","updates":[{{updates}}]}]}""");
    }

    private FlowEvent[] Flow()
    {
        var events = new FlowEvent[_flow.Capacity];
        return events[.._flow.Read(0, events).Count];
    }

    [Fact]
    public void A_snapshot_writes_no_book_changes()
    {
        Assert.Equal(HandleOutcome.Synced, Handle(Snapshot));

        Assert.Equal(0, _handler.BookChanges);
        Assert.Empty(Flow());
    }

    [Fact]
    public void Changes_near_the_mid_carry_the_signed_change_in_amount()
    {
        Handle(Snapshot);
        var book = _handler.Book;
        var bid = book.Bids.PriceAt(0);
        var bidAmount = book.Bids.AmountAt(0);
        var ask = book.Asks.PriceAt(0);

        Assert.Equal(HandleOutcome.Applied, Update(("bid", bid, bidAmount + 50_000_000), ("offer", ask + 100, 25_000_000)));
        Assert.Equal(HandleOutcome.Applied, Update(("bid", bid, 0)));

        Assert.Equal(
        [
            new FlowEvent(Arrival, FlowKind.BookChange, FlowSide.Buy, bid, 50_000_000),
            new FlowEvent(Arrival, FlowKind.BookChange, FlowSide.Sell, ask + 100, 25_000_000),
            new FlowEvent(Arrival, FlowKind.BookChange, FlowSide.Buy, bid, -(bidAmount + 50_000_000)),
        ], Flow());
        Assert.Equal(1, _handler.BookChanges);
    }

    [Fact]
    public void Changes_far_from_the_mid_are_counted_but_not_written()
    {
        Handle(Snapshot);
        var mid = (_handler.Book.Bids.PriceAt(0) + _handler.Book.Asks.PriceAt(0)) / 2;
        var edge = mid - mid * BookHandler.FlowBandBasisPoints / 10_000;

        Update(("bid", mid * 9 / 10, 1_000), ("bid", edge, 1_000), ("bid", edge - 1, 1_000));

        Assert.Equal(3, _handler.BookChanges);
        Assert.Equal([edge], Flow().Select(e => e.Price));
    }

    [Fact]
    public void An_unchanged_amount_is_no_change()
    {
        Handle(Snapshot);
        var bid = _handler.Book.Bids.PriceAt(0);

        Update(("bid", bid, _handler.Book.Bids.AmountAt(0)));

        Assert.Equal(0, _handler.BookChanges);
        Assert.Empty(Flow());
    }

    [Fact]
    public void Without_a_flow_ring_changes_are_still_counted()
    {
        _handler.Flow = null;
        Handle(Snapshot);

        Update(("bid", _handler.Book.Bids.PriceAt(0), 1));

        Assert.Equal(1, _handler.BookChanges);
        Assert.Equal(0, _flow.Published);
    }

    [Fact]
    public void Recorded_kraken_updates_write_changes_within_the_band()
    {
        var kraken = new KrakenAdapter().CreateBookHandler(new PairRules(1, "BTC/USD", 1, 8), new EngineOptions())!;
        var ring = new FlowRing(FlowRing.DefaultCapacity);
        kraken.Flow = ring;
        long changes = 0;
        foreach (var line in TestMessages.Sequence("kraken-book.jsonl"))
        {
            var buffer = TestMessages.Buffer(_pool, line);
            Assert.NotEqual(HandleOutcome.Failed, kraken.Handle(buffer));
            buffer.Release();
            changes += kraken.BookChanges;
        }

        var events = new FlowEvent[ring.Capacity];
        var read = ring.Read(0, events);
        Assert.InRange(read.Count + read.Lost, 100, changes);
        var mid = (kraken.Book.Bids.PriceAt(0) + kraken.Book.Asks.PriceAt(0)) / 2;
        // The mid moves a little over the recorded minute, so allow for that around the final one.
        Assert.All(events[..read.Count], e => Assert.InRange(Math.Abs(e.Price - mid), 0, mid * 30 / 10_000));
        Assert.All(events[..read.Count], e => Assert.NotEqual(0, e.Amount));
    }
}
