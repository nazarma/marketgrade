using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;

namespace Marketgrade.Engine.Tests.Books;

public class KrakenBookHandlerTests
{
    private static readonly PairRules s_rules = new(1, "BTC/USD", PriceDecimals: 1, AmountDecimals: 8);

    private readonly MessageBufferPool _pool = new(4, 1024);
    private readonly BookHandler _handler = new KrakenAdapter().CreateBookHandler(s_rules, new EngineOptions());

    [Fact]
    public void Recorded_sequence_passes_every_checksum()
    {
        var outcomes = Run(TestMessages.Sequence("kraken-book.jsonl"));

        Assert.Equal(1, outcomes[HandleOutcome.Synced]);
        Assert.Equal(1_189, outcomes[HandleOutcome.Applied]);
        Assert.False(outcomes.ContainsKey(HandleOutcome.Failed), _handler.Cause);
        var book = _handler.Book;
        Assert.Equal(BookState.Valid, book.State);
        Assert.Equal(KrakenAdapter.BookDepth, book.Bids.Count);
        Assert.Equal(KrakenAdapter.BookDepth, book.Asks.Count);
        Assert.True(book.Bids.PriceAt(0) < book.Asks.PriceAt(0));
        // Recorded 2026-10-25 from 22:23:17 to about 22:24 UTC.
        Assert.InRange(book.LastExchangeUnixNs, 1_792_966_997_000_000_000, 1_792_967_100_000_000_000);
        Assert.Equal(4, _pool.Available);
    }

    [Fact]
    public void Changed_quantity_fails_the_checksum_and_the_next_snapshot_repairs_the_book()
    {
        var lines = TestMessages.Sequence("kraken-book.jsonl");
        var snapshot = Array.FindIndex(lines, l => l.Contains("\"type\":\"snapshot\"", StringComparison.Ordinal));
        var update = lines[snapshot + 1];
        // A new best bid changes the top 10 levels the checksum covers.
        var tampered = update.Replace("\"bids\":[", "\"bids\":[{\"price\":80368.8,\"qty\":0.5},", StringComparison.Ordinal);
        Assert.NotEqual(update, tampered);

        Assert.Equal(HandleOutcome.Synced, Handle(lines[snapshot], connectionId: 1));
        Assert.Equal(HandleOutcome.Failed, Handle(tampered, connectionId: 1));
        Assert.Equal(FailureKind.Checksum, _handler.Failure);
        Assert.Contains("Checksum mismatch", _handler.Cause);
        Assert.Equal(BookState.Invalid, _handler.Book.State);

        // The rest of the broken connection is skipped, even correct messages.
        Assert.Equal(HandleOutcome.Ignored, Handle(lines[snapshot + 2], connectionId: 1));

        Assert.Equal(HandleOutcome.Ignored, Handle(lines[snapshot + 1], connectionId: 2));
        Assert.Equal(BookState.Syncing, _handler.Book.State);
        Assert.Equal(HandleOutcome.Synced, Handle(lines[snapshot], connectionId: 2));
        Assert.Equal(BookState.Valid, _handler.Book.State);
        Assert.Equal(HandleOutcome.Applied, Handle(lines[snapshot + 1], connectionId: 2));
    }

    [Fact]
    public void Heartbeats_and_control_messages_are_ignored()
    {
        Assert.Equal(HandleOutcome.Ignored, Handle("""{"channel":"heartbeat"}"""));
        Assert.Equal(MessageKind.Heartbeat, _handler.Kind);
        Assert.Equal(HandleOutcome.Ignored, Handle("""{"method":"pong","req_id":1}"""));
    }

    [Fact]
    public void Data_before_type_is_a_parse_failure()
    {
        Assert.Equal(HandleOutcome.Failed, Handle("""{"channel":"book","data":[{"bids":[],"asks":[],"checksum":0}],"type":"snapshot"}"""));
        Assert.Equal(FailureKind.Parse, _handler.Failure);
    }

    [Fact]
    public void Exponent_notation_is_a_parse_failure()
    {
        var message = """{"channel":"book","type":"snapshot","data":[{"bids":[{"price":45283.5,"qty":1e-8}],"asks":[],"checksum":0}]}""";

        Assert.Equal(HandleOutcome.Failed, Handle(message));
        Assert.Equal(FailureKind.Parse, _handler.Failure);
    }

    [Fact]
    public void Message_without_checksum_is_a_parse_failure()
    {
        Assert.Equal(HandleOutcome.Failed, Handle("""{"channel":"book","type":"snapshot","data":[{"bids":[],"asks":[]}]}"""));
        Assert.Equal(FailureKind.Parse, _handler.Failure);
    }

    private HandleOutcome Handle(string message, long connectionId = 1)
    {
        var buffer = TestMessages.Buffer(_pool, message, connectionId: connectionId);
        var outcome = _handler.Handle(buffer);
        buffer.Release();
        return outcome;
    }

    private Dictionary<HandleOutcome, int> Run(IEnumerable<string> messages)
    {
        var outcomes = new Dictionary<HandleOutcome, int>();
        foreach (var message in messages)
        {
            var outcome = Handle(message);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            if (outcome == HandleOutcome.Failed)
            {
                break;
            }
        }
        return outcomes;
    }
}
