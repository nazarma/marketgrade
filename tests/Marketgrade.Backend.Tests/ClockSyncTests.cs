using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Backend.Tests;

public class ClockSyncTests
{
    private readonly FakeClocks _time = new();
    private readonly List<string> _logs = [];
    private readonly FakeTarget _target = new();
    private readonly List<string> _queried = [];
    private bool _silent;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sync_sends_8_requests_and_keeps_the_shortest_round_trip()
    {
        double[] roundTripsMs = [40, 25, 31, 9, 12, 50, 18, 22];
        var sync = Sync((_, i) => Sample(roundTripsMs[i], offsetMs: i == 3 ? -7 : 30));

        var best = await sync.MeasureAsync(Token);

        Assert.Equal(8, _queried.Count);
        Assert.Equal(9, best!.Value.Sample.RoundTrip.TotalMilliseconds);
        Assert.Equal(-7, best.Value.Sample.Offset.TotalMilliseconds);
    }

    [Fact]
    public async Task Requests_that_fail_are_left_out_and_a_silent_server_hands_over_to_the_next()
    {
        var sync = Sync((server, i) => server == "time.apple.com" || i % 2 == 0
            ? throw new TimeoutException("no answer")
            : Sample(10 + i));

        var best = await sync.MeasureAsync(Token);

        Assert.Equal(16, _queried.Count);
        Assert.Equal(["time.apple.com", "pool.ntp.org"], _queried.Distinct());
        Assert.Equal((11, "pool.ntp.org"), (best!.Value.Sample.RoundTrip.TotalMilliseconds, best.Value.Server));
    }

    [Fact]
    public async Task Syncs_again_after_10_minutes()
    {
        var sync = Sync();
        await sync.TickAsync(Token);

        _time.Advance(ClockSync.Interval - TimeSpan.FromSeconds(1));
        await sync.TickAsync(Token);
        Assert.Single(_target.Syncs);

        _time.Advance(TimeSpan.FromSeconds(1));
        await sync.TickAsync(Token);
        Assert.Equal(2, _target.Syncs.Count);
    }

    [Fact]
    public async Task The_monotonic_clocks_rate_error_is_measured_and_applied()
    {
        // The monotonic clock runs 15 ppm slow: NTP time gets 9 ms ahead of it in 10 minutes.
        _time.RatePpm = 15;
        var sync = Sync();
        await sync.TickAsync(Token);
        _time.Advance(ClockSync.Interval + TimeSpan.FromSeconds(1));
        await sync.TickAsync(Token);

        Assert.Equal(["sync", "sync"], _target.Calls);
        Assert.Equal(0, _target.Syncs[0].Rate);
        Assert.Equal(15e-6, _target.Syncs[1].Rate, 1e-9);
    }

    [Fact]
    public void The_rate_needs_syncs_5_minutes_apart()
    {
        var rate = new ClockRate(1_000_000_000);
        rate.Add(0, 0);
        rate.Add(240_000_000_000 + 12_000_000, 240_000_000_000);
        Assert.Equal(0, rate.Rate);

        rate.Add(300_000_000_000 + 15_000_000, 300_000_000_000);
        Assert.Equal(50e-6, rate.Rate, 1e-12);

        rate.Reset();
        rate.Add(900_000_000_000, 900_000_000_000);
        Assert.Equal(0, rate.Rate);
    }

    [Theory]
    [InlineData(500, 200)]
    [InlineData(-500, -200)]
    [InlineData(-120, -120)]
    public void The_rate_is_clamped_to_200_ppm(double ppm, double expected)
    {
        var rate = new ClockRate(1_000_000_000);
        rate.Add(0, 0);
        var tenMinutes = 600_000_000_000L;
        rate.Add((long)(tenMinutes * (1 + ppm * 1e-6)), tenMinutes);

        Assert.Equal(expected * 1e-6, rate.Rate, 1e-12);
    }

    [Fact]
    public async Task A_step_of_more_than_20_ms_holds_delays_syncs_at_once_and_starts_the_rate_over()
    {
        _time.RatePpm = 15;
        var sync = await SyncWithKnownRateAsync();

        // A slow slew within the limit: no sync.
        _time.Advance(TimeSpan.FromSeconds(1), wallExtra: TimeSpan.FromMilliseconds(15));
        await sync.TickAsync(Token);
        Assert.Equal(2, _target.Syncs.Count);

        // macOS steps the wall clock by 2 s.
        _time.Advance(TimeSpan.FromSeconds(1), wallExtra: TimeSpan.FromSeconds(2));
        await sync.TickAsync(Token);
        Assert.Equal(["sync", "sync", "hold", "sync"], _target.Calls);
        Assert.Equal(0, _target.Syncs[2].Rate);
        Assert.Contains(_logs, l => l.StartsWith("Clock corrected by 2015.", StringComparison.Ordinal));

        // Measured from the new sync on, the clocks agree again.
        _time.Advance(TimeSpan.FromSeconds(1));
        await sync.TickAsync(Token);
        Assert.Equal(3, _target.Syncs.Count);
    }

    [Fact]
    public async Task Clocks_parting_slowly_sync_without_starting_the_rate_over_until_it_is_known()
    {
        // At 150 ppm the clocks part by 20 ms every 133 s until the rate is known; then they do not.
        _time.RatePpm = 150;
        var sync = Sync();
        for (var i = 0; i < 20 * 60; i++)
        {
            await sync.TickAsync(Token);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(150e-6, _target.Syncs[^1].Rate, 1e-9);
        var learned = _target.Syncs.FindIndex(s => s.Rate != 0);
        Assert.InRange(learned, 2, 4);
        Assert.Equal(learned + 2, _target.Syncs.Count);
        Assert.DoesNotContain(_target.Syncs.Skip(learned), s => s.Rate == 0);
    }

    [Fact]
    public async Task A_wake_from_sleep_holds_delays_and_syncs_at_once_without_reporting_the_sleep_as_a_correction()
    {
        _time.RatePpm = 15;
        var sync = await SyncWithKnownRateAsync();

        _time.Sleep(TimeSpan.FromMinutes(30));
        await sync.TickAsync(Token);

        Assert.Equal(["sync", "sync", "hold", "sync"], _target.Calls);
        Assert.Equal(0, _target.Syncs[2].Rate);
        Assert.Contains("Clock re-synced after sleep", _logs);
        Assert.DoesNotContain(_logs, l => l.StartsWith("Clock corrected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task After_sleep_without_an_answer_the_last_sync_is_dropped()
    {
        var sync = Sync();
        await sync.TickAsync(Token);
        _silent = true;

        _time.Sleep(TimeSpan.FromMinutes(30));
        await sync.TickAsync(Token);

        Assert.Equal(["sync", "hold", "clear"], _target.Calls);
    }

    [Fact]
    public async Task After_a_step_without_an_answer_the_last_sync_is_given_back_and_not_retried_every_second()
    {
        var sync = Sync();
        await sync.TickAsync(Token);
        _silent = true;
        _time.Advance(TimeSpan.FromSeconds(1), wallExtra: TimeSpan.FromSeconds(2));
        await sync.TickAsync(Token);

        Assert.Equal(["sync", "hold", "sync"], _target.Calls);
        Assert.Equal(_target.Syncs[0], _target.Syncs[1]);
        Assert.Contains("No time server answered; delays stay on the last sync", _logs);

        var queried = _queried.Count;
        _time.Advance(TimeSpan.FromSeconds(30));
        await sync.TickAsync(Token);
        Assert.Equal(queried, _queried.Count);

        _silent = false;
        _time.Advance(TimeSpan.FromSeconds(30));
        await sync.TickAsync(Token);
        Assert.Equal(3, _target.Syncs.Count);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task Two_failed_syncs_in_a_row_start_the_rate_over(int failures, bool rateKept)
    {
        _time.RatePpm = 15;
        var sync = await SyncWithKnownRateAsync();
        _silent = true;
        for (var i = 0; i < failures; i++)
        {
            _time.Advance(i == 0 ? ClockSync.Interval + TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(61));
            await sync.TickAsync(Token);
        }
        _silent = false;
        _time.Advance(TimeSpan.FromSeconds(61));
        await sync.TickAsync(Token);

        Assert.Equal(rateKept, _target.Syncs[^1].Rate != 0);
    }

    /// <summary>Two syncs 10 minutes apart, so the rate is known.</summary>
    private async Task<ClockSync> SyncWithKnownRateAsync()
    {
        var sync = Sync();
        await sync.TickAsync(Token);
        _time.Advance(ClockSync.Interval + TimeSpan.FromSeconds(1));
        await sync.TickAsync(Token);
        Assert.NotEqual(0, _target.Syncs[1].Rate);
        return sync;
    }

    private ClockSync Sync(Func<string, int, SntpSample>? answer = null)
    {
        var requests = 0;
        return new ClockSync(_target, new ListLogger(_logs), _time, () => _time.SleepNs, (server, _) =>
        {
            _queried.Add(server);
            var i = requests++ % ClockSync.Requests;
            return _silent ? throw new SocketException() : Task.FromResult(answer?.Invoke(server, i) ?? Sample(10));
        }, TimeSpan.Zero);
    }

    /// <summary>An answer at the fake time: NTP time, or <paramref name="offsetMs"/> before the wall clock.</summary>
    private SntpSample Sample(double roundTripMs, double? offsetMs = null)
    {
        var wall = (_time.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
        var ntp = offsetMs is { } offset ? wall - (long)(offset * 1_000_000) : _time.NtpUnixNs;
        return new SntpSample(TimeSpan.FromMilliseconds(roundTripMs), _time.GetTimestamp(), ntp, wall);
    }

    /// <summary>
    /// A wall clock, a monotonic clock that stops during sleep and runs <see cref="RatePpm"/> slow, a clock
    /// that counts sleep, and true NTP time, all in nanoseconds.
    /// </summary>
    private sealed class FakeClocks : TimeProvider
    {
        private DateTimeOffset _wall = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);
        private long _timestamp = 1_000_000_000;

        public double RatePpm { get; set; }

        public long NtpUnixNs { get; private set; } = (new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks) * 100;

        public long SleepNs { get; private set; } = 5_000_000_000;

        public override long TimestampFrequency => 1_000_000_000;

        public override DateTimeOffset GetUtcNow() => _wall;

        public override long GetTimestamp() => _timestamp;

        /// <summary>Real time passes: NTP time, the wall clock (plus <paramref name="wallExtra"/>) and the slow monotonic clock.</summary>
        public void Advance(TimeSpan elapsed, TimeSpan wallExtra = default)
        {
            var ns = elapsed.Ticks * 100;
            NtpUnixNs += ns;
            SleepNs += ns;
            _timestamp += (long)(ns / (1 + RatePpm * 1e-6));
            _wall += elapsed + wallExtra;
        }

        /// <summary>The computer sleeps: everything but the monotonic clock goes on.</summary>
        public void Sleep(TimeSpan duration)
        {
            NtpUnixNs += duration.Ticks * 100;
            SleepNs += duration.Ticks * 100;
            _wall += duration;
        }
    }

    private sealed class FakeTarget : IClockTarget
    {
        public List<string> Calls { get; } = [];

        public List<(long NtpUnixNs, long Timestamp, double Rate)> Syncs { get; } = [];

        public void Sync(long ntpUnixNs, long timestamp, double rate)
        {
            Calls.Add("sync");
            Syncs.Add((ntpUnixNs, timestamp, rate));
        }

        public void Hold() => Calls.Add("hold");

        public void Clear() => Calls.Add("clear");
    }

    private sealed class ListLogger(List<string> logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Add(formatter(state, exception));
    }
}
