using Marketgrade.Engine.Books;
using Marketgrade.Engine.History;

namespace Marketgrade.Engine.Tests.History;

public class MinuteRecorderTests
{
    private static readonly long s_sleepNs = (long)TimeSpan.FromSeconds(5).TotalNanoseconds;
    private static readonly DateTime s_noon = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
    private static readonly PairRules s_rules = new(2, "BTC-USD", 2, 8);

    private readonly MinuteRecorder _recorder = new(2, "Coinbase", s_sleepNs);

    private static long At(double secondsAfterNoon) => (s_noon.Ticks - DateTime.UnixEpoch.Ticks) * 100 + (long)(secondsAfterNoon * 1e9);

    private MinuteStats? Advance(double seconds, bool live = true, BookState state = BookState.Valid, long reconnects = 0) =>
        _recorder.Advance(At(seconds), live, state, reconnects, 8_100_000, 8_100_001, s_rules);

    /// <summary>Housekeeping every 250 ms from <paramref name="from"/> up to, not including, <paramref name="to"/>; nothing ends in between.</summary>
    private void Run(double from, double to, bool live = true, BookState state = BookState.Valid)
    {
        for (var t = from; t < to - 1e-9; t += 0.25)
        {
            Assert.Null(Advance(t, live, state));
        }
    }

    [Fact]
    public void The_first_minute_counts_from_the_start_and_ends_at_the_boundary()
    {
        Run(30, 60);
        _recorder.BookUpdates = 1_000;
        _recorder.Trades = 20;
        _recorder.Bytes = 300_000;

        var minute = Advance(60.25);

        Assert.NotNull(minute);
        Assert.Equal(s_noon, minute.MinuteUtc);
        Assert.Equal(DateTimeKind.Utc, minute.MinuteUtc.Kind);
        Assert.Equal(30, minute.SecondsRunning, 6);
        Assert.Equal(30, minute.SecondsLive, 6);
        Assert.Equal(30, minute.SecondsValid, 6);
        Assert.Equal((1_000L, 20L, 300_000L), (minute.BookUpdates, minute.Trades, minute.Bytes));
        Assert.Equal((2, "Coinbase"), (minute.ExchangeId, minute.Exchange));
    }

    [Fact]
    public void A_pause_across_the_boundary_is_split_between_the_minutes()
    {
        Run(0, 59.9);
        Assert.Null(Advance(59.9));

        var first = Advance(60.2);
        Run(60.45, 120);
        var second = Advance(120);

        Assert.Equal(60, first!.SecondsRunning, 6);
        Assert.Equal(60, second!.SecondsRunning, 6);
        Assert.Equal(s_noon.AddMinutes(1), second.MinuteUtc);
    }

    [Fact]
    public void Time_asleep_or_with_the_clock_moved_back_is_not_running_time()
    {
        Run(0, 10);
        Assert.Null(Advance(10));
        Assert.Null(Advance(40));
        Assert.Null(Advance(35));
        Run(35.25, 60);

        var minute = Advance(60);

        // 0 to 10 and 35 to 60.
        Assert.Equal(35, minute!.SecondsRunning, 6);
    }

    [Fact]
    public void A_minute_slept_through_entirely_has_no_statistics()
    {
        Run(0, 60);
        Assert.NotNull(Advance(60));

        Assert.Null(Advance(200));
        Run(200.25, 240);
        var after = Advance(240);

        Assert.Equal(s_noon.AddMinutes(3), after!.MinuteUtc);
        Assert.Equal(40, after.SecondsRunning, 6);
    }

    [Fact]
    public void Seconds_follow_the_connection_and_book_state_of_each_pass()
    {
        Run(0, 10, live: true, state: BookState.Syncing);
        Run(10, 40, live: true, state: BookState.Valid);
        Run(40, 50, live: false, state: BookState.Invalid);
        Run(50, 60, live: true, state: BookState.Invalid);

        // Each pass adds the time since the previous one in the state it sees.
        var minute = Advance(60, live: true, state: BookState.Syncing);

        Assert.Equal(60, minute!.SecondsRunning, 6);
        Assert.Equal(50, minute.SecondsLive, 6);
        Assert.Equal(30, minute.SecondsValid, 6);
        Assert.Equal(20, minute.SecondsInvalid, 6);
    }

    [Fact]
    public void Delays_go_into_percentiles_maximum_and_coarse_counts_and_start_again_each_minute()
    {
        Run(0, 60);
        for (var ms = 1; ms <= 100; ms++)
        {
            _recorder.Delay.Record(ms * 1_000_000L);
        }
        _recorder.Processing.Record(7_000);
        _recorder.Hold.Record(34_000_000);
        _recorder.CatchUp = 12;

        var minute = Advance(60)!;
        Run(60.25, 120);
        var next = Advance(120)!;

        Assert.Equal(100, minute.DelayCount);
        Assert.Equal(50, minute.DelayP50Ms!.Value, 0);
        Assert.Equal(99, minute.DelayP99Ms!.Value, 0);
        Assert.Equal(100, minute.DelayMaxMs);
        Assert.Equal(100, minute.DelayHistogram.Sum());
        Assert.Equal(CoarseHistogram.Buckets, minute.DelayHistogram.Length);
        Assert.Equal(7_000, minute.ProcessingP50Ns!.Value, 7_000 * 0.02);
        Assert.Equal(34, minute.HoldP50Ms!.Value, 34 * 0.02);
        Assert.Equal(12, minute.CatchUpMessages);
        Assert.Equal(0, minute.DelayNegative);
        Assert.Null(minute.DelayAheadMs);

        Assert.Equal(0, next.DelayCount);
        Assert.Null(next.DelayP50Ms);
        Assert.Null(next.DelayMaxMs);
        Assert.Null(next.ProcessingP50Ns);
        Assert.Null(next.HoldP50Ms);
        Assert.Equal(0, next.DelayHistogram.Sum());
        Assert.Equal(0, next.CatchUpMessages);
    }

    [Fact]
    public void Negative_delays_are_counted_with_how_far_below_zero_they_went()
    {
        Run(0, 60);
        for (var ms = 1; ms <= 10; ms++)
        {
            _recorder.Delay.Record(-ms * 1_000_000L);
            _recorder.Ahead.Record(ms * 1_000_000L);
        }

        var minute = Advance(60)!;
        Run(60.25, 120);
        var next = Advance(120)!;

        Assert.Equal(10, minute.DelayCount);
        Assert.Equal(10, minute.DelayNegative);
        Assert.Equal(10, minute.DelayAheadMs!.Value, 10 * 0.02);
        Assert.Equal(0, next.DelayNegative);
        Assert.Null(next.DelayAheadMs);
    }

    [Fact]
    public void Reconnects_and_gaps_are_counted_per_minute()
    {
        Assert.Null(Advance(0, reconnects: 4));
        Assert.Null(Advance(20, reconnects: 5));
        _recorder.GapsStarted = 1;
        Assert.Null(Advance(25, reconnects: 6));

        var minute = Advance(60, reconnects: 6);
        Run(60.25, 120);
        var next = Advance(120, reconnects: 6);

        Assert.Equal((2, 1), (minute!.Reconnects, minute.GapsStarted));
        Assert.Equal((0, 0), (next!.Reconnects, next.GapsStarted));
    }

    [Fact]
    public void Closing_prices_keep_the_pair_decimals()
    {
        Run(0, 60);

        var minute = _recorder.Advance(At(60), true, BookState.Valid, 0, 8_117_677, 8_117_678, s_rules)!;
        Run(60.25, 120);
        var invalid = _recorder.Advance(At(120), true, BookState.Invalid, 0, 0, 0, s_rules)!;

        Assert.Equal(81_176.77m, minute.BestBid);
        Assert.Equal(81_176.78m, minute.BestAsk);
        Assert.Equal(0.01m, minute.Spread);
        Assert.Equal("0.01", minute.Spread!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Null(invalid.BestBid);
        Assert.Null(invalid.Spread);
    }

    [Fact]
    public void Flush_returns_the_minute_so_far()
    {
        Assert.Null(_recorder.Flush(At(0), true, BookState.Valid, 0, 0, 0, s_rules));

        Run(0, 15);
        _recorder.BookUpdates = 3;
        var partial = _recorder.Flush(At(15), true, BookState.Valid, 0, 0, 0, s_rules);

        Assert.Equal(s_noon, partial!.MinuteUtc);
        Assert.Equal(15, partial.SecondsRunning, 6);
        Assert.Equal(3, partial.BookUpdates);
    }
}
