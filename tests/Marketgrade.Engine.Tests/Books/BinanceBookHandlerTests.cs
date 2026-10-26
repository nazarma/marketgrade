using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;

namespace Marketgrade.Engine.Tests.Books;

public class BinanceBookHandlerTests
{
    private static readonly PairRules s_rules = new(3, "BTCUSDT", PriceDecimals: 2, AmountDecimals: 5);

    private readonly MessageBufferPool _pool = new(8, 1024);
    private readonly BinanceBookHandler _handler = (BinanceBookHandler)new BinanceAdapter().CreateBookHandler(s_rules, new EngineOptions());
    private long _seq;

    public BinanceBookHandlerTests()
    {
        // A fetch starts once the connection's first message has arrived, as in the book worker.
        Assert.Equal(HandleOutcome.Ignored, Handle("""{"result":null,"id":1}"""));
    }

    [Fact]
    public void Stale_held_events_are_dropped_and_the_first_event_continues_the_snapshot()
    {
        _handler.StartFetch(0);
        Assert.Equal(HandleOutcome.Held, Handle(Event(90, 95, bid: "100.00")));
        Assert.Equal(HandleOutcome.Held, Handle(Event(96, 100, bid: "101.00")));
        Assert.Equal(HandleOutcome.Held, Handle(Event(98, 103, bid: "102.00")));
        Assert.Equal(HandleOutcome.Held, Handle(Event(104, 104, bid: "103.00")));
        Assert.Equal(4, _handler.HeldCount);

        Assert.Equal(HandleOutcome.Synced, Snapshot(100, bid: "99.00"));

        Assert.Equal(BookState.Valid, _handler.Book.State);
        Assert.Equal(0, _handler.HeldCount);
        Assert.Equal([10300L, 10200L, 9900L], Prices(_handler.Book.Bids));
        Assert.Equal(_pool.Created, _pool.Available);
        Assert.Equal(HandleOutcome.Applied, Handle(Event(105, 106)));
    }

    [Fact]
    public void Snapshot_older_than_every_held_event_fails_and_needs_a_new_one()
    {
        _handler.StartFetch(0);
        Handle(Event(105, 110));

        Assert.Equal(HandleOutcome.Failed, Snapshot(100));

        Assert.Equal(FailureKind.Sequence, _handler.Failure);
        Assert.StartsWith("First event after the snapshot starts at U 105, needs at most 101", _handler.Cause);
        Assert.True(_handler.NeedsSnapshot);
        Assert.Equal(_pool.Created, _pool.Available);
    }

    [Fact]
    public void First_event_may_come_after_the_snapshot()
    {
        _handler.StartFetch(0);
        Assert.Equal(HandleOutcome.Ignored, Snapshot(100));
        Assert.Equal(BookState.Syncing, _handler.Book.State);

        Assert.Equal(HandleOutcome.Ignored, Handle(Event(95, 100)));
        Assert.Equal(HandleOutcome.Synced, Handle(Event(99, 101)));
    }

    [Fact]
    public void Gap_after_the_first_event_invalidates_the_book_and_asks_for_a_snapshot()
    {
        Sync(lastUpdateId: 100);
        Assert.Equal(HandleOutcome.Applied, Handle(Event(102, 110)));

        Assert.Equal(HandleOutcome.Failed, Handle(Event(112, 115)));

        Assert.Equal(FailureKind.Sequence, _handler.Failure);
        Assert.StartsWith("Update ID gap: expected U 111, got 112", _handler.Cause);
        Assert.Equal(BookState.Invalid, _handler.Book.State);
        Assert.True(_handler.NeedsSnapshot);
        // Until a fetch starts, events are not kept.
        Assert.Equal(HandleOutcome.Ignored, Handle(Event(116, 120)));
        Assert.Equal(0, _handler.HeldCount);
    }

    [Fact]
    public void Book_invalidated_from_outside_waits_for_a_new_snapshot()
    {
        Sync(lastUpdateId: 100);

        _handler.Invalidate();

        // A message of the lost connection still in the queue must not make the book valid again.
        Assert.Equal(HandleOutcome.Ignored, Handle(Event(102, 105)));
        Assert.Equal(BookState.Invalid, _handler.Book.State);
        Assert.True(_handler.NeedsSnapshot);
    }

    [Fact]
    public void Old_and_overlapping_events_follow_the_documented_rule()
    {
        Sync(lastUpdateId: 100);
        Assert.Equal(HandleOutcome.Applied, Handle(Event(102, 110)));

        Assert.Equal(HandleOutcome.Ignored, Handle(Event(105, 110)));
        Assert.Equal(HandleOutcome.Ignored, Handle(Event(100, 108)));
        Assert.Equal(HandleOutcome.Applied, Handle(Event(108, 112, ask: "200.00")));
        Assert.Equal(HandleOutcome.Applied, Handle(Event(113, 113)));
        Assert.Equal(BookState.Valid, _handler.Book.State);
    }

    [Fact]
    public void Fetches_are_at_least_ten_seconds_apart_and_respect_a_backoff()
    {
        Assert.True(_handler.ShouldFetch(0));
        _handler.StartFetch(0);
        Assert.False(_handler.ShouldFetch(1));

        _handler.FetchFailed(retryAtMs: 0);
        Assert.False(_handler.ShouldFetch(9_999));
        Assert.True(_handler.ShouldFetch(10_000));

        // HTTP 429 with Retry-After: 60.
        _handler.StartFetch(10_000);
        _handler.FetchFailed(retryAtMs: 70_000);
        Assert.False(_handler.ShouldFetch(69_999));
        Assert.True(_handler.ShouldFetch(70_000));
        Assert.Equal(2, _handler.Resyncs);
    }

    [Fact]
    public void Held_events_beyond_the_limit_are_released_and_holding_starts_over()
    {
        var pool = new MessageBufferPool(BinanceBookHandler.MaxHeld + 1, 512);
        _handler.StartFetch(0);
        for (var i = 0; i < BinanceBookHandler.MaxHeld; i++)
        {
            Assert.Equal(HandleOutcome.Held, Handle(Event(i * 2, i * 2 + 1), pool));
        }
        Assert.Equal(1, pool.Available);

        Assert.Equal(HandleOutcome.Held, Handle(Event(4_000, 4_001), pool));

        Assert.Equal(1, _handler.HeldCount);
        Assert.Equal(1, _handler.HeldOverflows);
        Assert.Equal(pool.Created - 1, pool.Available);
        _handler.ReleaseHeld();
        Assert.Equal(pool.Created, pool.Available);
    }

    [Fact]
    public void New_connection_releases_held_events_and_starts_over()
    {
        _handler.StartFetch(0);
        Handle(Event(101, 102));
        Assert.Equal(1, _handler.HeldCount);

        Assert.Equal(HandleOutcome.Ignored, Handle(Event(500, 501), connectionId: 2));

        Assert.Equal(0, _handler.HeldCount);
        Assert.True(_handler.NeedsSnapshot);
        Assert.Equal(_pool.Created, _pool.Available);
        Assert.Equal(HandleOutcome.Ignored, Snapshot(100));
        Assert.True(_handler.ShouldFetch(10_000));
    }

    [Fact]
    public void Recorded_events_chain_without_a_gap()
    {
        var events = TestMessages.Sequence("binance-depth.jsonl");
        var firstU = long.Parse(events[0].Split("\"U\":")[1].Split(',')[0], System.Globalization.CultureInfo.InvariantCulture);
        Sync(lastUpdateId: firstU - 1, ownEvent: false);

        var outcomes = events.Select(e => Handle(e)).ToList();

        Assert.Equal(HandleOutcome.Synced, outcomes[0]);
        Assert.All(outcomes.Skip(1), o => Assert.Equal(HandleOutcome.Applied, o));
        Assert.True(_handler.Book.Bids.Count > 0 && _handler.Book.Asks.Count > 0);
        Assert.InRange(_handler.Book.LastExchangeUnixNs, 1_792_966_997_000_000_000, 1_792_967_100_000_000_000);
    }

    [Fact]
    public void Error_body_instead_of_a_snapshot_is_a_parse_failure()
    {
        _handler.StartFetch(0);
        Handle(Event(101, 102));

        Assert.Equal(HandleOutcome.Failed, SnapshotText("""{"code":-1003,"msg":"Too many requests"}"""));

        Assert.Equal(FailureKind.Parse, _handler.Failure);
        Assert.True(_handler.NeedsSnapshot);
        Assert.Equal(_pool.Created, _pool.Available);
    }

    private void Sync(long lastUpdateId, bool ownEvent = true)
    {
        _handler.StartFetch(0);
        Snapshot(lastUpdateId, bid: "99.00", ask: "101.00");
        if (ownEvent)
        {
            Assert.Equal(HandleOutcome.Synced, Handle(Event(lastUpdateId, lastUpdateId + 1)));
        }
    }

    private HandleOutcome Snapshot(long lastUpdateId, string bid = "99.00", string ask = "101.00") =>
        SnapshotText($$"""{"lastUpdateId":{{lastUpdateId}},"bids":[["{{bid}}","1.00000000"]],"asks":[["{{ask}}","1.00000000"]]}""");

    private HandleOutcome SnapshotText(string text)
    {
        var snapshot = TestMessages.Buffer(_pool, text);
        var outcome = _handler.OnSnapshot(snapshot);
        snapshot.Release();
        return outcome;
    }

    private static string Event(long first, long last, string? bid = null, string? ask = null) =>
        $$"""{"e":"depthUpdate","E":1792966997514,"s":"BTCUSDT","U":{{first}},"u":{{last}},"b":[{{Level(bid)}}],"a":[{{Level(ask)}}]}""";

    private static string Level(string? price) => price is null ? "" : $"""["{price}","0.50000000"]""";

    private HandleOutcome Handle(string message, MessageBufferPool? pool = null, long connectionId = 1)
    {
        var buffer = TestMessages.Buffer(pool ?? _pool, message, ++_seq, connectionId);
        var outcome = _handler.Handle(buffer);
        if (outcome != HandleOutcome.Held)
        {
            buffer.Release();
        }
        return outcome;
    }

    private static List<long> Prices(OrderBookSide side) => [.. Enumerable.Range(0, side.Count).Select(side.PriceAt)];
}
