using System.Text;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Recording;
using Marketgrade.Engine.Tests.Recording;

namespace Marketgrade.Engine.Tests.Books;

public class BookReplayTests
{
    private const long Second = 1_000_000_000;

    private static readonly PairRules s_kraken = new(1, "BTC/USD", 1, 8);
    private static readonly PairRules s_binance = new(3, "BTCUSDT", 2, 5);

    [Fact]
    public void Failure_followed_by_a_live_book_reset_is_explained()
    {
        var replay = Replay(new KrakenAdapter(), s_kraken, KrakenWithBadUpdate(withLiveReset: true));

        var failure = Assert.Single(replay.Failures);
        Assert.Equal(FailureKind.Checksum, failure.Kind);
        Assert.True(failure.Explained);
        Assert.Equal(0, replay.UnexplainedFailures);
        Assert.Single(replay.LiveResets);
        // The second connection's snapshot rebuilt the book.
        Assert.Equal(2, replay.Syncs);
        Assert.Equal(BookState.Valid, replay.Book.State);
    }

    [Fact]
    public void Failure_the_live_book_did_not_have_is_unexplained()
    {
        var replay = Replay(new KrakenAdapter(), s_kraken, KrakenWithBadUpdate(withLiveReset: false));

        Assert.False(Assert.Single(replay.Failures).Explained);
        Assert.Equal(1, replay.UnexplainedFailures);
    }

    [Fact]
    public void Recorded_binance_snapshot_rebuilds_the_book()
    {
        var records = BinanceRecords(snapshotAfter: 3, secondsPerEvent: 0.1);

        var replay = Replay(new BinanceAdapter(), s_binance, records);

        Assert.Empty(replay.Failures);
        Assert.Empty(replay.MissingSnapshots);
        Assert.Equal(1, replay.Syncs);
        Assert.Equal(BookState.Valid, replay.Book.State);
        // The first event comes before the fetch starts, two are older than the snapshot.
        Assert.Equal(147, replay.Applied);
    }

    [Fact]
    public void Binance_without_a_recorded_snapshot_reports_the_range_instead_of_failures()
    {
        // As if the recorder's queue was full when the live worker wrote its snapshot.
        var records = BinanceRecords(snapshotAfter: null, secondsPerEvent: 0.2);

        var replay = Replay(new BinanceAdapter(), s_binance, records);

        Assert.Empty(replay.Failures);
        var missing = Assert.Single(replay.MissingSnapshots);
        // All but the first, which arrives before holding starts, as live.
        Assert.Equal(149, missing.Events);
        Assert.Equal(0, replay.Applied);
    }

    private static BookReplay Replay(ExchangeAdapter adapter, PairRules rules, IEnumerable<TestRecord> records)
    {
        var replay = new BookReplay(adapter, rules);
        using var reader = new RecordingReader(new MemoryStream(TestRecordings.Build(records, adapter.Id, adapter.Name)));
        replay.Read(reader);
        replay.Finish();
        return replay;
    }

    private static List<TestRecord> KrakenWithBadUpdate(bool withLiveReset)
    {
        var lines = TestMessages.Sequence("kraken-book.jsonl");
        var records = new List<TestRecord> { Opened(1, 0, 1) };
        long seq = 0;
        records.Add(Message(++seq, lines[2], 1, 1));
        records.Add(Message(++seq, lines[3].Replace("\"bids\":[", "\"bids\":[{\"price\":80368.8,\"qty\":0.5},", StringComparison.Ordinal), 2, 1));
        if (withLiveReset)
        {
            records.Add(new(RecordType.BookReset, seq, 2, 1, Encoding.UTF8.GetBytes("Checksum mismatch")));
        }
        records.Add(Opened(seq, 3, 2));
        records.Add(Message(++seq, lines[2], 4, 2));
        records.Add(Message(++seq, lines[3], 5, 2));
        return records;
    }

    private static List<TestRecord> BinanceRecords(int? snapshotAfter, double secondsPerEvent)
    {
        var events = TestMessages.Sequence("binance-depth.jsonl");
        var records = new List<TestRecord> { Opened(0, 0, 1) };
        for (var i = 0; i < events.Length; i++)
        {
            var arrival = (long)(i * secondsPerEvent * Second);
            records.Add(Message(i + 1, events[i], arrival, 1));
            if (i == snapshotAfter)
            {
                // Taken right after the third event: the fourth continues it.
                var lastU = events[2].Split("\"u\":")[1].Split(',')[0];
                var snapshot = $$"""{"lastUpdateId":{{lastU}},"bids":[["80000.00","1.00000000"]],"asks":[["80001.00","1.00000000"]]}""";
                records.Add(new(RecordType.Snapshot, i + 1, arrival, 1, Encoding.UTF8.GetBytes(snapshot)));
            }
        }
        return records;
    }

    private static TestRecord Opened(long seq, long arrival, long connectionId) =>
        new(RecordType.ConnectionOpened, seq, arrival * Second, connectionId, "wss://example.test"u8.ToArray());

    private static TestRecord Message(long seq, string text, long arrival, long connectionId) =>
        new(RecordType.Message, seq, arrival, connectionId, Encoding.UTF8.GetBytes(text));
}
