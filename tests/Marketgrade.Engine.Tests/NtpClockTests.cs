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
        Assert.False(_clock.TryUnixNs(Stopwatch.GetTimestamp(), out _));
        Assert.Null(_clock.OffsetNs(0, Stopwatch.GetTimestamp()));

        var synced = Stopwatch.GetTimestamp();
        _clock.Sync(1_000 * Ms, synced);

        Assert.True(_clock.TryUnixNs(synced + Ticks(250 * Ms), out var later));
        Assert.InRange(later, 1_250 * Ms - 1_000, 1_250 * Ms + 1_000);
        Assert.InRange(_clock.OffsetNs(1_210 * Ms, synced + Ticks(250 * Ms))!.Value, -40 * Ms - 1_000, -40 * Ms + 1_000);
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

    public void Dispose() => _http.Dispose();

    private static long Ticks(long ns) => (long)(ns * (Stopwatch.Frequency / 1e9));

    private void Process(string message, long wallUnixNs, long timestamp)
    {
        var buffer = TestMessages.Buffer(_pool, message, connectionId: 1, arrivalUnixNs: wallUnixNs);
        buffer.ReceivedTimestamp = timestamp;
        _worker.Process(buffer);
    }

    private static long Timestamp(string message)
    {
        using var json = JsonDocument.Parse(message);
        var text = json.RootElement.GetProperty("data")[0].GetProperty("timestamp").GetString()!;
        Assert.True(Iso8601.TryParseUnixNs(System.Text.Encoding.ASCII.GetBytes(text), out var unixNs));
        return unixNs;
    }
}
