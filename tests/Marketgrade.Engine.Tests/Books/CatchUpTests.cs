using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine.Tests.Books;

/// <summary>Messages within 100 ms after a snapshot are catch-up: counted apart, not in the delay statistics.</summary>
public sealed class CatchUpTests : IDisposable
{
    private const long Ms = 1_000_000;

    private readonly string[] _lines = TestMessages.Sequence("kraken-book.jsonl");
    private readonly MessageBufferPool _pool = new(8, 16 * 1024);
    private readonly HttpClient _http = new();
    private readonly BookWorker _worker;

    public CatchUpTests()
    {
        var adapter = new KrakenAdapter();
        var options = new EngineOptions();
        var events = new ConnectionEventLog(50);
        var queue = Channel.CreateUnbounded<MessageBuffer>();
        var connection = new ExchangeConnection(adapter, _pool, [queue.Writer], options, events);
        _worker = new BookWorker(adapter, connection, queue.Reader, new ManualResetEventSlim(), _pool, _http, new NtpClock(),
            options, events, NullLogger.Instance);
        _worker.Prepare(new PairRules(1, "BTC/USD", 1, 8));
    }

    [Fact]
    public void Window_includes_the_snapshot_and_ends_exactly_100_ms_after_it()
    {
        var snapshot = Timestamp(_lines[2]);
        var arrival = snapshot + 900 * Ms;

        Process(_lines[2], arrival);
        Process(_lines[3], arrival + 100 * Ms);
        // One microsecond later, above any monotonic clock's tick.
        Process(_lines[4], arrival + 100 * Ms + 1_000);
        Process(_lines[5], arrival + 500 * Ms);

        Assert.Equal(2, _worker.CatchUpMessages);
        Assert.Equal(Math.Max(900 * Ms, arrival + 100 * Ms - Timestamp(_lines[3])), _worker.CatchUpMaxDelayNs);
        _worker.PublishStatistics(Stopwatch.GetTimestamp());
        Assert.Equal(2, _worker.DelayCount);
    }

    [Fact]
    public void Next_snapshot_starts_the_counters_again()
    {
        var arrival = Timestamp(_lines[2]) + 700 * Ms;
        Process(_lines[2], arrival);
        Process(_lines[3], arrival + 10 * Ms);
        Assert.Equal(2, _worker.CatchUpMessages);

        // A new connection sends a new snapshot.
        Process(_lines[2], arrival + 60_000 * Ms, connectionId: 2);

        Assert.Equal(1, _worker.CatchUpMessages);
        Assert.Equal(60_700 * Ms, _worker.CatchUpMaxDelayNs);
    }

    [Theory]
    [InlineData(2_000)]
    [InlineData(-2_000)]
    public void A_wall_clock_step_right_after_the_snapshot_moves_nothing_out_of_the_window(long stepMs)
    {
        var arrival = Timestamp(_lines[2]) + 900 * Ms;

        Process(_lines[2], arrival);
        // macOS sets the wall clock right after the snapshot; the monotonic clock goes on.
        Process(_lines[3], arrival + 50 * Ms, wallStepNs: stepMs * Ms);
        Process(_lines[4], arrival + 500 * Ms, wallStepNs: stepMs * Ms);

        Assert.Equal(2, _worker.CatchUpMessages);
        _worker.PublishStatistics(Stopwatch.GetTimestamp());
        Assert.Equal(1, _worker.DelayCount);
    }

    public void Dispose() => _http.Dispose();

    /// <param name="wallStepNs">How far the wall clock was set since the start; the monotonic arrival stays at <paramref name="arrivalUnixNs"/>.</param>
    private void Process(string message, long arrivalUnixNs, long connectionId = 1, long wallStepNs = 0)
    {
        var buffer = TestMessages.Buffer(_pool, message, connectionId: connectionId, arrivalUnixNs: arrivalUnixNs);
        buffer.ArrivalUnixNs += wallStepNs;
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
