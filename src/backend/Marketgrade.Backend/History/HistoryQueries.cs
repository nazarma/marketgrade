using Marketgrade.Engine.Books;

namespace Marketgrade.Backend.History;

/// <param name="Points">Oldest first; a minute without a row is a minute the app was not running.</param>
public sealed record ExchangeDelays(int Id, string Exchange, IReadOnlyList<DelayPoint> Points);

public sealed record DelayPoint(DateTime MinuteUtc, double? P50Ms, double? P99Ms);

public sealed record DelayHistory(DateTime FromUtc, DateTime ToUtc, IReadOnlyList<ExchangeDelays> Exchanges);

/// <summary>Per-minute state of one exchange as parallel arrays, oldest first: only minutes the app ran.</summary>
/// <param name="MinuteMs">Start of each minute, Unix milliseconds.</param>
public sealed record MinuteColumns(long[] MinuteMs, double[] Running, double[] Valid, double[] Invalid, int[] Reconnects, int[] Gaps);

public sealed record ReconnectEntry(DateTime TimeUtc, string Reason);

/// <param name="Uptime">Seconds with a valid book divided by seconds the app was running, 0 to 1; null if it never ran in the range.</param>
/// <param name="DelayP50Ms">From the minutes' coarse histograms added up, so approximate (within about 12 %); likewise <paramref name="DelayP99Ms"/>.</param>
/// <param name="DelayMaxMs">The largest delay of any minute, exact.</param>
public sealed record ExchangeHealth(
    int Id,
    string Exchange,
    double? Uptime,
    double RunningSeconds,
    double ValidSeconds,
    double? DelayP50Ms,
    double? DelayP99Ms,
    double? DelayMaxMs,
    long BookUpdates,
    long Trades,
    MinuteColumns Minutes,
    IReadOnlyList<ReconnectEntry> Reconnects);

public sealed record HealthHistory(DateTime FromUtc, DateTime ToUtc, IReadOnlyList<ExchangeHealth> Exchanges);

public sealed record GapEntry(
    int ExchangeId,
    string Exchange,
    DateTime StartUtc,
    DateTime EndUtc,
    double DurationSeconds,
    string Kind,
    string Cause,
    string RepairAction);

/// <summary>Reads the history store for the history endpoints. Every exchange of the engine is listed, also one without rows.</summary>
public sealed class HistoryQueries(HistoryStore store, IReadOnlyList<(int Id, string Name)> exchanges, TimeProvider? time = null)
{
    public const int MaxGaps = 500;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public DelayHistory Delay(int minutes)
    {
        var (from, to) = Range(TimeSpan.FromMinutes(minutes));
        // Two rows of one minute (a restart within it): the one with more delays measured.
        var rows = store.Query("""
            SELECT exchange_id, minute, arg_max(delay_p50_ms, delay_count), arg_max(delay_p99_ms, delay_count)
            FROM minute_stats WHERE minute >= $from
            GROUP BY exchange_id, minute ORDER BY minute
            """,
            r => (Id: r.GetInt32(0), Point: new DelayPoint(Utc(r.GetDateTime(1)), Double(r, 2), Double(r, 3))),
            ("from", from));
        return new DelayHistory(from, to, [.. exchanges.Select(e =>
            new ExchangeDelays(e.Id, e.Name, [.. rows.Where(r => r.Id == e.Id).Select(r => r.Point)]))]);
    }

    public HealthHistory Health(int hours)
    {
        var (from, to) = Range(TimeSpan.FromHours(hours));
        var minutes = store.Query("""
            SELECT exchange_id, minute, sum(seconds_running), sum(seconds_valid), sum(seconds_invalid),
                sum(reconnects)::BIGINT, sum(gaps_started)::BIGINT, sum(book_updates)::BIGINT, sum(trades)::BIGINT,
                max(delay_max_ms)
            FROM minute_stats WHERE minute >= $from
            GROUP BY exchange_id, minute ORDER BY minute
            """,
            r => new
            {
                Id = r.GetInt32(0),
                Minute = Utc(r.GetDateTime(1)),
                Running = r.GetDouble(2),
                Valid = r.GetDouble(3),
                Invalid = r.GetDouble(4),
                Reconnects = (int)r.GetInt64(5),
                Gaps = (int)r.GetInt64(6),
                BookUpdates = r.GetInt64(7),
                Trades = r.GetInt64(8),
                MaxMs = Double(r, 9),
            },
            ("from", from));
        var histograms = store.Query(
            "SELECT exchange_id, delay_histogram FROM minute_stats WHERE minute >= $from AND delay_count > 0",
            r => (Id: r.GetInt32(0), Counts: r.GetFieldValue<List<long>>(1)),
            ("from", from));
        var reconnects = store.Query(
            "SELECT exchange_id, time_utc, reason FROM reconnects WHERE time_utc >= $from ORDER BY time_utc",
            r => (Id: r.GetInt32(0), Entry: new ReconnectEntry(Utc(r.GetDateTime(1)), r.GetString(2))),
            ("from", from));

        return new HealthHistory(from, to, [.. exchanges.Select(e =>
        {
            var rows = minutes.Where(m => m.Id == e.Id).ToList();
            var coarse = new long[CoarseHistogram.Buckets];
            foreach (var (_, counts) in histograms.Where(h => h.Id == e.Id))
            {
                for (var i = 0; i < Math.Min(coarse.Length, counts.Count); i++)
                {
                    coarse[i] += counts[i];
                }
            }
            var running = rows.Sum(m => m.Running);
            var valid = rows.Sum(m => m.Valid);
            return new ExchangeHealth(
                e.Id,
                e.Name,
                running > 0 ? Math.Min(1, valid / running) : null,
                running,
                valid,
                CoarseHistogram.PercentileMs(coarse, 50),
                CoarseHistogram.PercentileMs(coarse, 99),
                rows.Max(m => m.MaxMs),
                rows.Sum(m => m.BookUpdates),
                rows.Sum(m => m.Trades),
                new MinuteColumns(
                    [.. rows.Select(m => (m.Minute.Ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond)],
                    [.. rows.Select(m => m.Running)],
                    [.. rows.Select(m => m.Valid)],
                    [.. rows.Select(m => m.Invalid)],
                    [.. rows.Select(m => m.Reconnects)],
                    [.. rows.Select(m => m.Gaps)]),
                [.. reconnects.Where(r => r.Id == e.Id).Select(r => r.Entry)]);
        })]);
    }

    /// <summary>Gaps that ended in the range, newest first, at most <see cref="MaxGaps"/>.</summary>
    public IReadOnlyList<GapEntry> Gaps(int hours)
    {
        var (from, _) = Range(TimeSpan.FromHours(hours));
        return store.Query($"""
            SELECT exchange_id, exchange, start_utc, end_utc, kind, cause, repair_action
            FROM gaps WHERE end_utc >= $from ORDER BY start_utc DESC LIMIT {MaxGaps}
            """,
            r =>
            {
                var start = Utc(r.GetDateTime(2));
                var end = Utc(r.GetDateTime(3));
                return new GapEntry(r.GetInt32(0), r.GetString(1), start, end, (end - start).TotalSeconds, r.GetString(4), r.GetString(5), r.GetString(6));
            },
            ("from", from));
    }

    /// <summary>From the start of the minute <paramref name="length"/> ago, so the oldest minute is whole, to now.</summary>
    private (DateTime From, DateTime To) Range(TimeSpan length)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var from = now - length;
        return (new DateTime(from.Ticks - from.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc), now);
    }

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static double? Double(DuckDB.NET.Data.DuckDBDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
}
