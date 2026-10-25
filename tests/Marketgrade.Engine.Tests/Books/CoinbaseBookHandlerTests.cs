using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;

namespace Marketgrade.Engine.Tests.Books;

public class CoinbaseBookHandlerTests
{
    private static readonly PairRules s_rules = new(2, "BTC-USD", PriceDecimals: 2, AmountDecimals: 8);

    private readonly MessageBufferPool _pool = new(4, 1024);
    private readonly BookHandler _handler = new CoinbaseAdapter().CreateBookHandler(s_rules, new EngineOptions());

    private static string Snapshot => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coinbase", "data-l2-snapshot-trimmed.json"));

    /// <summary>Recorded messages 1 to 150 of a connection: l2_data, heartbeats and subscriptions in one sequence.</summary>
    private static string[] Recorded => TestMessages.Sequence("coinbase-after-snapshot.jsonl");

    [Fact]
    public void Recorded_sequence_counts_every_channel_and_passes()
    {
        Assert.Equal(HandleOutcome.Synced, Handle(Snapshot));
        var outcomes = Recorded.Select(m => Handle(m)).GroupBy(o => o).ToDictionary(g => g.Key, g => g.Count());

        Assert.False(outcomes.ContainsKey(HandleOutcome.Failed), _handler.Cause);
        Assert.Equal(141, outcomes[HandleOutcome.Applied]);
        // 7 heartbeats and 2 subscriptions messages keep the sequence going without touching the book.
        Assert.Equal(9, outcomes[HandleOutcome.Ignored]);
        Assert.Equal(BookState.Valid, _handler.Book.State);
        Assert.True(_handler.Book.Bids.Count > 0 && _handler.Book.Asks.Count > 0);
        Assert.InRange(_handler.Book.LastExchangeUnixNs, 1_792_966_997_000_000_000, 1_792_967_100_000_000_000);
    }

    [Fact]
    public void Missing_heartbeat_is_a_sequence_gap()
    {
        var recorded = Recorded;
        var heartbeat = Array.FindIndex(recorded, m => m.StartsWith("{\"channel\":\"heartbeats\"", StringComparison.Ordinal));
        Assert.Equal(HandleOutcome.Synced, Handle(Snapshot));
        for (var i = 0; i < heartbeat; i++)
        {
            Assert.NotEqual(HandleOutcome.Failed, Handle(recorded[i]));
        }

        Assert.Equal(HandleOutcome.Failed, Handle(recorded[heartbeat + 1]));

        Assert.Equal(FailureKind.Sequence, _handler.Failure);
        Assert.Equal($"Sequence gap: expected {heartbeat + 1}, got {heartbeat + 2} (message 1)", _handler.Cause);
        Assert.Equal(BookState.Invalid, _handler.Book.State);
        Assert.Equal(HandleOutcome.Ignored, Handle(recorded[heartbeat + 2]));
    }

    [Fact]
    public void New_connection_starts_a_new_sequence_from_its_snapshot()
    {
        Assert.Equal(HandleOutcome.Synced, Handle(Snapshot, connectionId: 1));
        Assert.Equal(HandleOutcome.Failed, Handle(Recorded[5], connectionId: 1));

        // The first number of a connection is its start, whatever it is.
        Assert.Equal(HandleOutcome.Synced, Handle(Snapshot.Replace("\"sequence_num\":0", "\"sequence_num\":1", StringComparison.Ordinal), connectionId: 2));
        Assert.Equal(HandleOutcome.Applied, Handle(Recorded[1], connectionId: 2));
        Assert.Equal(BookState.Valid, _handler.Book.State);
    }

    [Fact]
    public void Updates_before_the_snapshot_are_ignored()
    {
        Assert.Equal(HandleOutcome.Ignored, Handle(Recorded[0]));
        Assert.Equal(BookState.Syncing, _handler.Book.State);
        Assert.Equal(0, _handler.Book.Bids.Count + _handler.Book.Asks.Count);
    }

    [Fact]
    public void Error_messages_are_ignored()
    {
        Assert.Equal(HandleOutcome.Ignored, Handle("""{"type":"error","message":"subscribe or unsubscribe required"}"""));
        Assert.Equal(MessageKind.Error, _handler.Kind);
    }

    [Fact]
    public void Too_many_decimals_is_a_parse_failure()
    {
        var message = Recorded[0].Replace("\"new_quantity\":\"0.00832947\"", "\"new_quantity\":\"0.008329471\"", StringComparison.Ordinal);
        Assert.NotEqual(Recorded[0], message);
        Assert.Equal(HandleOutcome.Synced, Handle(Snapshot));

        Assert.Equal(HandleOutcome.Failed, Handle(message));
        Assert.Equal(FailureKind.Parse, _handler.Failure);
    }

    private HandleOutcome Handle(string message, long connectionId = 1)
    {
        var buffer = TestMessages.Buffer(_pool, message, connectionId: connectionId);
        var outcome = _handler.Handle(buffer);
        buffer.Release();
        return outcome;
    }
}
