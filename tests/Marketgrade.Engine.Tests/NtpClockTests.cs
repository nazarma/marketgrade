using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Tests.Books;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine.Tests;

public sealed class NtpClockTests : IDisposable
{
    private const long Ms = 1_000_000;

    private readonly string[] _lines = TestMessages.Sequence("kraken-book.jsonl");
    private readonly MessageBufferPool _pool = new(8, 16 * 1024);
    private readonly HttpClient _http = new();
    private readonly NtpClock _clock = new();
    private readonly BookWorker _worker;

    public NtpClockTests()
    {
        var adapter = new KrakenAdapter();
        var options = new EngineOptions();
        var events = new ConnectionEventLog(50);
        var queue = Channel.CreateUnbounded<MessageBuffer>();
        var connection = new ExchangeConnection(adapter, _pool, [queue.Writer], options, events);
        _worker = new BookWorker(adapter, connection, queue.Reader, new ManualResetEventSlim(), _pool, _http, _clock,
            options, events, NullLogger.Instance);
        _worker.Prepare(new PairRules(1, "BTC/USD", 1, 8));
    }

    [Fact]
    public void Ntp_time_is_the_synced_time_plus_the_monotonic_time_since()
    {
        Assert.Equal(ClockReading.Unknown, _clock.Read(Stopwatch.GetTimestamp(), out _));
        Assert.Null(_clock.OffsetNs(0, Stopwatch.GetTimestamp()));

        var synced = Stopwatch.GetTimestamp();
        _clock.Sync(1_000 * Ms, synced);

        Assert.Equal(ClockReading.Known, _clock.Read(synced + Ticks(250 * Ms), out var later));
        Assert.InRange(later, 1_250 * Ms - 1_000, 1_250 * Ms + 1_000);
        Assert.InRange(_clock.OffsetNs(1_210 * Ms, synced + Ticks(250 * Ms))!.Value, -40 * Ms - 1_000, -40 * Ms + 1_000);
    }

    [Fact]
    public void The_monotonic_clocks_rate_error_is_applied()
    {
        var synced = Stopwatch.GetTimestamp();
        // NTP time runs 120 ppm faster than the monotonic clock: 72 ms more in 10 minutes.
        _clock.Sync(0, synced, rate: 120e-6);

        _clock.Read(synced + Ticks(600_000 * Ms), out var later);

        Assert.InRange(later, 600_072 * Ms - 1_000, 600_072 * Ms + 1_000);
    }

    [Fact]
    public void Held_delays_are_skipped_and_counted_until_the_next_sync()
    {
        var snapshot = Timestamp(_lines[2]) - 10_000 * Ms;
        var synced = Stopwatch.GetTimestamp();
        _clock.Sync(snapshot, synced);
        Process(_lines[2], wallUnixNs: snapshot, timestamp: synced);
        void ProcessAt(int line)
        {
            var arrival = Timestamp(_lines[line]) + 20 * Ms;
            Process(_lines[line], wallUnixNs: arrival, timestamp: synced + Ticks(arrival - snapshot));
        }
        ProcessAt(3);

        _clock.Hold();
        Assert.Equal(ClockReading.Held, _clock.Read(synced, out _));
        Assert.Null(_clock.OffsetNs(0, synced));
        for (var i = 4; i < 9; i++)
        {
            ProcessAt(i);
        }
        _clock.Sync(snapshot, synced);
        ProcessAt(9);
        _worker.PublishStatistics(Stopwatch.GetTimestamp());

        Assert.Equal(2, _worker.DelayCount);
        Assert.Equal(5, _worker.DelaySkipped);
    }

    [Fact]
    public void Before_the_first_sync_a_hold_changes_nothing()
    {
        _clock.Hold();
        Assert.Equal(ClockReading.Unknown, _clock.Read(Stopwatch.GetTimestamp(), out _));
    }

    [Fact]
    public void A_2_second_step_of_the_wall_clock_changes_no_delay()
    {
        // Synced so that NTP time is the wall clock at first; every message arrives 20 ms after its timestamp.
        var snapshot = Timestamp(_lines[2]) - 10_000 * Ms;
        var synced = Stopwatch.GetTimestamp();
        _clock.Sync(snapshot, synced);
        Process(_lines[2], wallUnixNs: snapshot, timestamp: synced);
        for (var i = 0; i < 20; i++)
        {
            var arrival = Timestamp(_lines[3 + i]) + 20 * Ms;
            // Halfway, macOS sets the wall clock 2 s later; the monotonic clock goes on.
            var step = i < 10 ? 0 : 2_000 * Ms;
            Process(_lines[3 + i], wallUnixNs: arrival + step, timestamp: synced + Ticks(arrival - snapshot));
        }
        _worker.PublishStatistics(Stopwatch.GetTimestamp());

        Assert.Equal(20, _worker.DelayCount);
        Assert.Equal(0, _worker.DelayNegative);
        Assert.Equal(20 * Ms, _worker.DelayP50Ns, 20 * Ms * 0.02);
        Assert.Equal(20 * Ms, _worker.DelayP99Ns, 20 * Ms * 0.02);
    }

    [Fact]
    public void After_a_wake_resync_a_2_second_step_of_the_wall_clock_records_no_delay_above_the_true_one()
    {
        // Before sleep: synced, a message arrives 20 ms after its timestamp.
        var start = Timestamp(_lines[2]) - 10_000 * Ms;
        var synced = Stopwatch.GetTimestamp();
        _clock.Sync(start, synced);
        Process(_lines[2], wallUnixNs: start, timestamp: synced);

        // The wake: delays are held until the re-sync, so a message in between is skipped.
        _clock.Hold();
        var held = Timestamp(_lines[3]) + 20 * Ms;
        Process(_lines[3], wallUnixNs: held, timestamp: synced + Ticks(held - start));
        _clock.Sync(start, synced);

        // The new connection's snapshot arrives 2 s after its timestamp, and right after it macOS sets the wall
        // clock 2 s later. The updates sent meanwhile queued behind the snapshot and arrive with it (catch-up,
        // up to 2 s late); the later ones arrive 20 ms after their timestamps.
        var snapshot = Timestamp(_lines[2]) + 2_000 * Ms;
        Process(_lines[2], wallUnixNs: snapshot, timestamp: synced + Ticks(snapshot - start), connectionId: 2);
        var (catchUp, onTime, arrival) = (0, 0, snapshot);
        for (var i = 3; i < 600; i++)
        {
            // Heartbeats carry no timestamp and arrive with the message before them.
            if (TryTimestamp(_lines[i]) is { } sent)
            {
                var queued = sent + 20 * Ms <= snapshot + BookWorker.CatchUpWindowNs;
                arrival = queued ? snapshot + i * 1_000 : sent + 20 * Ms;
                (catchUp, onTime) = queued ? (catchUp + 1, onTime) : (catchUp, onTime + 1);
            }
            Process(_lines[i], wallUnixNs: arrival + 2_000 * Ms, timestamp: synced + Ticks(arrival - start), connectionId: 2);
        }
        _worker.PublishStatistics(Stopwatch.GetTimestamp());

        Assert.True(catchUp > 100 && onTime > 100, $"{catchUp} catch-up, {onTime} on time");
        Assert.Equal(1, _worker.DelaySkipped);
        Assert.Equal(catchUp + 1, _worker.CatchUpMessages);
        Assert.Equal(onTime, _worker.DelayCount);
        Assert.Equal(20 * Ms, _worker.DelayP50Ns, 20 * Ms * 0.02);
        Assert.Equal(20 * Ms, _worker.DelayP99Ns, 20 * Ms * 0.02);
        // The worst delay, as Data health shows it, is the true one (within a tick of the monotonic clock).
        Assert.InRange(_worker.Minutes.Delay.Max, 20 * Ms - 1_000, 20 * Ms + 1_000);
    }

    public void Dispose() => _http.Dispose();

    private static long Ticks(long ns) => (long)(ns * (Stopwatch.Frequency / 1e9));

    private void Process(string message, long wallUnixNs, long timestamp, long connectionId = 1)
    {
        var buffer = TestMessages.Buffer(_pool, message, connectionId: connectionId, arrivalUnixNs: wallUnixNs);
        buffer.ReceivedTimestamp = timestamp;
        _worker.Process(buffer);
    }

    private static long? TryTimestamp(string message)
    {
        using var json = JsonDocument.Parse(message);
        return json.RootElement.TryGetProperty("data", out var data) && data[0].TryGetProperty("timestamp", out _)
            ? Timestamp(message)
            : null;
    }

    private static long Timestamp(string message)
    {
        using var json = JsonDocument.Parse(message);
        var text = json.RootElement.GetProperty("data")[0].GetProperty("timestamp").GetString()!;
        Assert.True(Iso8601.TryParseUnixNs(System.Text.Encoding.ASCII.GetBytes(text), out var unixNs));
        return unixNs;
    }
}
