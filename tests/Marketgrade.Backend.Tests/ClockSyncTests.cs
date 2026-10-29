using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Backend.Tests;

public class ClockSyncTests
{
    private const long Ms = 1_000_000;

    private readonly FakeClocks _time = new();
    private readonly List<string> _logs = [];
    private readonly List<SntpSample> _applied = [];
    private readonly List<string> _queried = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sync_sends_8_requests_and_keeps_the_shortest_round_trip()
    {
        double[] roundTripsMs = [40, 25, 31, 9, 12, 50, 18, 22];
        var sync = Sync((_, i) => i == 3 ? Sample(roundTripsMs[i], offsetMs: -7) : Sample(roundTripsMs[i], offsetMs: 30));

        var best = await sync.MeasureAsync(Token);

        Assert.Equal(8, _queried.Count);
        Assert.Equal(9, best!.Value.RoundTrip.TotalMilliseconds);
        Assert.Equal(-7, best.Value.Offset.TotalMilliseconds);
    }

    [Fact]
    public async Task Requests_that_fail_are_left_out_and_a_silent_server_hands_over_to_the_next()
    {
        var sync = Sync((server, i) => server == "time.apple.com" || i % 2 == 0
            ? throw new TimeoutException("no answer")
            : Sample(10 + i, offsetMs: 5));

        var best = await sync.MeasureAsync(Token);

        Assert.Equal(16, _queried.Count);
        Assert.Equal(["time.apple.com", "pool.ntp.org"], _queried.Distinct());
        Assert.Equal(11, best!.Value.RoundTrip.TotalMilliseconds);
    }

    [Fact]
    public async Task Clocks_parting_by_more_than_20_ms_sync_at_once()
    {
        var sync = Sync((_, _) => Sample(10, offsetMs: 0));
        await sync.TickAsync(Token);
        Assert.Single(_applied);

        // A slow slew within the limit: no sync.
        _time.Advance(TimeSpan.FromSeconds(1), wallExtra: TimeSpan.FromMilliseconds(15));
        await sync.TickAsync(Token);
        Assert.Single(_applied);

        // macOS steps the wall clock by 2 s.
        _time.Advance(TimeSpan.FromSeconds(1), wallExtra: TimeSpan.FromSeconds(2));
        await sync.TickAsync(Token);
        Assert.Equal(2, _applied.Count);
        Assert.Contains("Clock corrected by 2015.0 ms", _logs);

        // Measured from the new sync on, the clocks agree again.
        _time.Advance(TimeSpan.FromSeconds(1));
        await sync.TickAsync(Token);
        Assert.Equal(2, _applied.Count);
    }

    [Fact]
    public async Task Syncs_again_after_10_minutes()
    {
        var sync = Sync((_, _) => Sample(10, offsetMs: 0));
        await sync.TickAsync(Token);

        _time.Advance(ClockSync.Interval - TimeSpan.FromSeconds(1));
        await sync.TickAsync(Token);
        Assert.Single(_applied);

        _time.Advance(TimeSpan.FromSeconds(1));
        await sync.TickAsync(Token);
        Assert.Equal(2, _applied.Count);
    }

    [Fact]
    public async Task Without_an_answer_it_tries_again_after_a_minute_and_does_not_count_parted_clocks_meanwhile()
    {
        var silent = false;
        var sync = Sync((_, _) => silent ? throw new SocketException() : Sample(10, offsetMs: 0));
        await sync.TickAsync(Token);
        silent = true;
        _time.Advance(TimeSpan.FromSeconds(1), wallExtra: TimeSpan.FromSeconds(2));
        await sync.TickAsync(Token);
        var queried = _queried.Count;

        _time.Advance(TimeSpan.FromSeconds(30));
        await sync.TickAsync(Token);
        Assert.Equal(queried, _queried.Count);

        silent = false;
        _time.Advance(TimeSpan.FromSeconds(30));
        await sync.TickAsync(Token);
        Assert.Equal(2, _applied.Count);
        Assert.Contains("No time server answered; delays stay on the last sync", _logs);
    }

    private ClockSync Sync(Func<string, int, SntpSample> answer)
    {
        var requests = 0;
        return new ClockSync(_applied.Add, new ListLogger(_logs), _time, (server, _) =>
        {
            _queried.Add(server);
            return Task.FromResult(answer(server, requests++ % ClockSync.Requests));
        }, TimeSpan.Zero);
    }

    /// <summary>An answer at the fake time, with the wall clock <paramref name="offsetMs"/> ahead of the server's.</summary>
    private SntpSample Sample(double roundTripMs, double offsetMs)
    {
        var wall = (_time.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
        return new SntpSample(TimeSpan.FromMilliseconds(roundTripMs), _time.GetTimestamp(), wall - (long)(offsetMs * Ms), wall);
    }

    /// <summary>A wall clock and a monotonic clock that can part.</summary>
    private sealed class FakeClocks : TimeProvider
    {
        private DateTimeOffset _wall = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp = 1_000_000_000;

        public override long TimestampFrequency => 1_000_000_000;

        public override DateTimeOffset GetUtcNow() => _wall;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed, TimeSpan wallExtra = default)
        {
            _timestamp += elapsed.Ticks * 100;
            _wall += elapsed + wallExtra;
        }
    }

    private sealed class ListLogger(List<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Add(formatter(state, exception));
    }
}
