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
    public void Hold_time_is_the_timestamp_minus_the_oldest_event_time()
    {
        Assert.Equal(HandleOutcome.Synced, Handle(Snapshot));
        Assert.Equal(BookHandler.NoHold, _handler.ExchangeHoldNs);

        Assert.Equal(HandleOutcome.Applied, Handle(Update(1, "22:23:17.312558822", "22:23:17.300000", "22:23:17.262558822", "22:23:17.310000")));

        Assert.Equal(50_000_000, _handler.ExchangeHoldNs);
    }

    [Fact]
    public void Hold_time_of_recorded_messages_matches_their_times()
    {
        Assert.Equal(HandleOutcome.Synced, Handle(Snapshot));
        foreach (var message in Recorded.Take(50))
        {
            Handle(message);
            using var json = System.Text.Json.JsonDocument.Parse(message);
            var root = json.RootElement;
            if (root.GetProperty("channel").GetString() != "l2_data")
            {
                Assert.Equal(BookHandler.NoHold, _handler.ExchangeHoldNs);
                continue;
            }
            var oldest = root.GetProperty("events").EnumerateArray()
                .SelectMany(e => e.GetProperty("updates").EnumerateArray())
                .Min(u => Ns(u.GetProperty("event_time").GetString()!));
            Assert.Equal(Ns(root.GetProperty("timestamp").GetString()!) - oldest, _handler.ExchangeHoldNs);
        }
    }

    [Fact]
    public void Unreadable_event_time_is_left_out_without_touching_the_book()
    {
        Assert.Equal(HandleOutcome.Synced, Handle(Snapshot));

        Assert.Equal(HandleOutcome.Applied, Handle(Update(1, "22:23:17.312558822", "not a time", "22:23:17.302558822")));
        Assert.Equal(10_000_000, _handler.ExchangeHoldNs);

        Assert.Equal(HandleOutcome.Applied, Handle(Update(2, "22:23:17.400000", "yesterday")));
        Assert.Equal(BookHandler.NoHold, _handler.ExchangeHoldNs);
        Assert.Equal(BookState.Valid, _handler.Book.State);
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

    /// <summary>An update message on 2026-10-25 with one bid update per event time; a time without a colon is written as is.</summary>
    private static string Update(long sequence, string timestamp, params string[] eventTimes)
    {
        var updates = eventTimes.Select((time, i) =>
            $$"""{"side":"bid","event_time":"{{(time.Contains(':') ? $"2026-10-25T{time}Z" : time)}}","price_level":"{{80000 + i}}.00","new_quantity":"0.5"}""");
        return $$"""{"channel":"l2_data","timestamp":"2026-10-25T{{timestamp}}Z","sequence_num":{{sequence}},"events":[{"type":"update","product_id":"BTC-USD","updates":[{{string.Join(",", updates)}}]}]}""";
    }

    private static long Ns(string time)
    {
        Assert.True(Iso8601.TryParseUnixNs(System.Text.Encoding.ASCII.GetBytes(time), out var unixNs));
        return unixNs;
    }

    private HandleOutcome Handle(string message, long connectionId = 1)
    {
        var buffer = TestMessages.Buffer(_pool, message, connectionId: connectionId);
        var outcome = _handler.Handle(buffer);
        buffer.Release();
        return outcome;
    }
}
