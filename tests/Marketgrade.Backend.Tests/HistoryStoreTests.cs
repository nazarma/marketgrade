using Marketgrade.Backend.History;
using Marketgrade.Engine.History;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Marketgrade.Backend.Tests;

public sealed class HistoryStoreTests : IDisposable
{
    private static readonly DateTime s_noon = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

    private readonly TempFolder _folder = new();
    private readonly HistoryStore _store;

    public HistoryStoreTests() => _store = new HistoryStore(Path.Combine(_folder.Path, "history.duckdb"));

    public void Dispose()
    {
        _store.Dispose();
        _folder.Dispose();
    }

    private long Count(string table) => _store.Query($"SELECT count(*) FROM {table}", r => r.GetInt64(0)).Single();

    [Fact]
    public void A_minute_reads_back_with_its_histogram_prices_and_utc_time()
    {
        var minute = HistoryTestData.Minute(2, s_noon, valid: 58.5, invalid: 1.5, delayMs: 40);

        _store.Add([minute, HistoryTestData.Minute(1, s_noon)]);

        var row = _store.Query(
            "SELECT minute, seconds_valid, delay_histogram, best_bid, spread, hold_p50_ms FROM minute_stats WHERE exchange_id = 2",
            r => (Minute: r.GetDateTime(0), Valid: r.GetDouble(1), Histogram: r.GetFieldValue<List<long>>(2),
                Bid: r.GetDecimal(3), Spread: r.GetDecimal(4), HoldNull: r.IsDBNull(5))).Single();
        Assert.Equal(s_noon, DateTime.SpecifyKind(row.Minute, DateTimeKind.Utc));
        Assert.Equal(58.5, row.Valid);
        Assert.Equal(minute.DelayHistogram, row.Histogram);
        Assert.Equal(81_176.77m, row.Bid);
        Assert.Equal(0.01m, row.Spread);
        Assert.True(row.HoldNull);
        Assert.Equal(2, Count("minute_stats"));
    }

    [Fact]
    public void Gaps_and_reconnects_are_stored()
    {
        _store.Add(new GapRecord(1, "Kraken", s_noon, s_noon.AddSeconds(0.8), GapKind.Checksum, "Checksum mismatch: ...", "Reconnected"));
        _store.Add(new ReconnectRecord(2, "Coinbase", s_noon, "Closed by server: 1006"));

        Assert.Equal(1, Count("gaps"));
        Assert.Equal("Checksum", _store.Query("SELECT kind FROM gaps", r => r.GetString(0)).Single());
        Assert.Equal("Closed by server: 1006", _store.Query("SELECT reason FROM reconnects", r => r.GetString(0)).Single());
    }

    [Fact]
    public void Retention_deletes_only_what_is_older()
    {
        _store.Add([HistoryTestData.Minute(1, s_noon.AddDays(-91)), HistoryTestData.Minute(1, s_noon.AddDays(-89))]);
        _store.Add(new GapRecord(1, "Kraken", s_noon.AddDays(-95), s_noon.AddDays(-95), GapKind.Parse, "old", "x"));
        _store.Add(new GapRecord(1, "Kraken", s_noon.AddDays(-1), s_noon.AddDays(-1), GapKind.Parse, "new", "x"));
        _store.Add(new ReconnectRecord(1, "Kraken", s_noon.AddDays(-100), "old"));

        Assert.Equal(1, _store.DeleteBefore(s_noon.AddDays(-90)));

        Assert.Equal(1, Count("minute_stats"));
        Assert.Equal("new", _store.Query("SELECT cause FROM gaps", r => r.GetString(0)).Single());
        Assert.Equal(0, Count("reconnects"));
    }

    [Fact]
    public void Reopening_keeps_the_rows_and_the_schema()
    {
        _store.Add([HistoryTestData.Minute(1, s_noon)]);
        _store.Dispose();

        using var reopened = new HistoryStore(Path.Combine(_folder.Path, "history.duckdb"));

        Assert.Equal(1, reopened.Query("SELECT count(*) FROM minute_stats", r => r.GetInt64(0)).Single());
        Assert.Equal(1, reopened.Query("SELECT max(version) FROM schema", r => r.GetInt32(0)).Single());
    }

    [Fact]
    public async Task The_writer_saves_a_minute_in_one_batch_and_what_is_queued_at_stop()
    {
        var feed = new HistoryFeed();
        var time = new FakeTimeProvider(s_noon.AddMinutes(1));
        var writer = new HistoryWriter(feed, _store, NullLogger<HistoryWriter>.Instance, time);
        await writer.StartAsync(TestContext.Current.CancellationToken);

        feed.Add(HistoryTestData.Minute(1, s_noon));
        feed.Add(HistoryTestData.Minute(2, s_noon));
        feed.Add(new ReconnectRecord(1, "Kraken", s_noon, "Dead after 10 s silence"));
        // The writer gathers the minute's rows for a second before inserting them.
        await WaitUntilAsync(() => Count("reconnects") == 1);
        Assert.Equal(0, Count("minute_stats"));
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => Count("minute_stats") == 2);

        feed.Add(HistoryTestData.Minute(1, s_noon.AddMinutes(1)));
        await writer.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, Count("minute_stats"));
    }

    [Fact]
    public async Task The_writer_deletes_old_history_at_start_and_when_the_setting_changes()
    {
        _store.Add([HistoryTestData.Minute(1, s_noon.AddDays(-100)), HistoryTestData.Minute(1, s_noon.AddDays(-20))]);
        var writer = new HistoryWriter(new HistoryFeed(), _store, NullLogger<HistoryWriter>.Instance, new FakeTimeProvider(s_noon));

        await writer.StartAsync(TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => Count("minute_stats") == 1);
        writer.KeepDays = 7;

        Assert.Equal(0, Count("minute_stats"));
        await writer.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
