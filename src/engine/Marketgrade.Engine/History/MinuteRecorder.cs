using Marketgrade.Engine.Books;

namespace Marketgrade.Engine.History;

/// <summary>
/// Collects one exchange's statistics for the current UTC minute, separate from the one-minute window of
/// the status: its own preallocated histograms and counters, which the book worker fills per message. The
/// worker calls <see cref="Advance"/> at every housekeeping pass, which adds the time since the last pass to
/// the connection and book state of that moment and, once a minute is over, returns its statistics and starts
/// the next one. Single-threaded; nothing is allocated except the published <see cref="MinuteStats"/>.
/// </summary>
internal sealed class MinuteRecorder(int exchangeId, string exchange, long asleepAfterNs)
{
    private const long MinuteNs = 60_000_000_000;

    private long _minute = long.MinValue;
    private long _last;
    private long _runningNs;
    private long _liveNs;
    private long _validNs;
    private long _invalidNs;
    private long _reconnectsAtStart;

    public LatencyHistogram Delay { get; } = new();

    /// <summary>How far below zero each negative delay was.</summary>
    public LatencyHistogram Ahead { get; } = new();

    public LatencyHistogram Processing { get; } = new();

    public LatencyHistogram Hold { get; } = new();

    public long BookUpdates;
    public long Trades;
    public long Bytes;
    public long CatchUp;
    public int GapsStarted;

    /// <param name="nowUnixNs">Wall-clock time; a pause longer than the limit (the computer slept) or a clock moved back counts as not running.</param>
    /// <param name="reconnects">The connection's running count of live connections that ended.</param>
    /// <param name="bid">Best bid and ask, scaled; 0 while the book is not valid.</param>
    /// <returns>The minute that just ended, or null while it goes on.</returns>
    public MinuteStats? Advance(long nowUnixNs, bool live, BookState state, long reconnects, long bid, long ask, PairRules? rules)
    {
        if (_minute == long.MinValue)
        {
            Start(nowUnixNs, reconnects);
            return null;
        }
        var elapsed = nowUnixNs - _last;
        if (elapsed < 0 || elapsed > asleepAfterNs)
        {
            elapsed = 0;
        }
        _last = nowUnixNs;

        var minute = Math.DivRem(nowUnixNs, MinuteNs).Quotient;
        if (minute == _minute)
        {
            Add(elapsed, live, state);
            return null;
        }
        // The part of the pause before the boundary belongs to the minute that ended.
        var sinceBoundary = Math.Min(elapsed, nowUnixNs - minute * MinuteNs);
        Add(elapsed - sinceBoundary, live, state);
        var ended = Close(reconnects, bid, ask, rules);
        _minute = minute;
        Add(sinceBoundary, live, state);
        return ended;
    }

    /// <summary>The minute so far, as the engine stops; null if nothing happened in it.</summary>
    public MinuteStats? Flush(long nowUnixNs, bool live, BookState state, long reconnects, long bid, long ask, PairRules? rules)
    {
        if (_minute == long.MinValue)
        {
            return null;
        }
        var ended = Advance(nowUnixNs, live, state, reconnects, bid, ask, rules);
        return ended ?? Close(reconnects, bid, ask, rules);
    }

    private void Start(long nowUnixNs, long reconnects)
    {
        _minute = Math.DivRem(nowUnixNs, MinuteNs).Quotient;
        _last = nowUnixNs;
        _reconnectsAtStart = reconnects;
    }

    private void Add(long ns, bool live, BookState state)
    {
        _runningNs += ns;
        if (live)
        {
            _liveNs += ns;
        }
        if (state == BookState.Valid)
        {
            _validNs += ns;
        }
        else if (state == BookState.Invalid)
        {
            _invalidNs += ns;
        }
    }

    private MinuteStats? Close(long reconnects, long bid, long ask, PairRules? rules)
    {
        var reconnected = (int)(reconnects - _reconnectsAtStart);
        MinuteStats? stats = null;
        if (_runningNs > 0 || BookUpdates > 0 || Trades > 0 || Bytes > 0 || reconnected > 0 || GapsStarted > 0)
        {
            var coarse = new long[CoarseHistogram.Buckets];
            Delay.AddTo(coarse);
            var delays = Delay.Count > 0;
            var holds = Hold.Count > 0;
            var processing = Processing.Count > 0;
            var decimals = rules?.PriceDecimals ?? 0;
            stats = new MinuteStats(
                exchangeId,
                exchange,
                DateTime.UnixEpoch.AddTicks(_minute * MinuteNs / 100),
                _runningNs / 1e9,
                _liveNs / 1e9,
                _validNs / 1e9,
                _invalidNs / 1e9,
                BookUpdates,
                Trades,
                Bytes,
                Delay.Count,
                delays ? Delay.Percentile(50) / 1e6 : null,
                delays ? Delay.Percentile(99) / 1e6 : null,
                delays ? Delay.Max / 1e6 : null,
                coarse,
                processing ? Processing.Percentile(50) : null,
                processing ? Processing.Percentile(99) : null,
                holds ? Hold.Percentile(50) / 1e6 : null,
                holds ? Hold.Percentile(99) / 1e6 : null,
                CatchUp,
                reconnected,
                GapsStarted,
                bid == 0 ? null : Price(bid, decimals),
                ask == 0 ? null : Price(ask, decimals),
                bid == 0 || ask == 0 ? null : Price(ask - bid, decimals),
                Ahead.Count,
                Ahead.Count > 0 ? Ahead.Percentile(99) / 1e6 : null);
        }

        Delay.Clear();
        Ahead.Clear();
        Processing.Clear();
        Hold.Clear();
        BookUpdates = Trades = Bytes = CatchUp = 0;
        GapsStarted = 0;
        _runningNs = _liveNs = _validNs = _invalidNs = 0;
        _reconnectsAtStart = reconnects;
        return stats;
    }

    private static decimal Price(long scaled, int decimals)
    {
        var magnitude = (ulong)Math.Abs(scaled);
        return new decimal((int)(uint)magnitude, (int)(uint)(magnitude >> 32), 0, scaled < 0, (byte)decimals);
    }
}
