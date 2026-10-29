using DuckDB.NET.Data;
using Marketgrade.Engine.History;

namespace Marketgrade.Backend.History;

/// <summary>
/// Chart history in DuckDB at <c>&lt;data folder&gt;/history.duckdb</c>: one row per exchange and minute the app ran
/// (<c>minute_stats</c>), every completed gap (<c>gaps</c>) and every reconnect (<c>reconnects</c>). Times are UTC.
/// One connection, used under a lock: the writer adds a few rows a minute and reads are rare, so nothing waits long.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    /// <summary>Applied in order; the <c>schema</c> table holds the number applied so far.</summary>
    private static readonly string[] s_migrations =
    [
        """
        CREATE TABLE minute_stats (
            exchange_id INTEGER NOT NULL,
            exchange VARCHAR NOT NULL,
            minute TIMESTAMP NOT NULL,
            seconds_running DOUBLE NOT NULL,
            seconds_live DOUBLE NOT NULL,
            seconds_valid DOUBLE NOT NULL,
            seconds_invalid DOUBLE NOT NULL,
            book_updates BIGINT NOT NULL,
            trades BIGINT NOT NULL,
            bytes BIGINT NOT NULL,
            delay_count BIGINT NOT NULL,
            delay_p50_ms DOUBLE,
            delay_p99_ms DOUBLE,
            delay_max_ms DOUBLE,
            delay_histogram BIGINT[] NOT NULL,
            processing_p50_ns BIGINT,
            processing_p99_ns BIGINT,
            hold_p50_ms DOUBLE,
            hold_p99_ms DOUBLE,
            catch_up_messages BIGINT NOT NULL,
            reconnects INTEGER NOT NULL,
            gaps_started INTEGER NOT NULL,
            best_bid DECIMAL(18, 8),
            best_ask DECIMAL(18, 8),
            spread DECIMAL(18, 8)
        );
        CREATE INDEX minute_stats_minute ON minute_stats (minute);
        CREATE TABLE gaps (
            exchange_id INTEGER NOT NULL,
            exchange VARCHAR NOT NULL,
            start_utc TIMESTAMP NOT NULL,
            end_utc TIMESTAMP NOT NULL,
            kind VARCHAR NOT NULL,
            cause VARCHAR NOT NULL,
            repair_action VARCHAR NOT NULL
        );
        CREATE TABLE reconnects (
            exchange_id INTEGER NOT NULL,
            exchange VARCHAR NOT NULL,
            time_utc TIMESTAMP NOT NULL,
            reason VARCHAR NOT NULL
        );
        """,
        """
        -- DuckDB cannot add a NOT NULL column; the default fills the rows already there.
        ALTER TABLE minute_stats ADD COLUMN delay_negative BIGINT DEFAULT 0;
        ALTER TABLE minute_stats ADD COLUMN delay_ahead_ms DOUBLE;
        """,
    ];

    private readonly Lock _lock = new();
    private readonly DuckDBConnection _connection;

    public HistoryStore(string path)
    {
        _connection = new DuckDBConnection($"Data Source={path}");
        _connection.Open();
        Migrate();
    }

    /// <summary>
    /// Adds one minute's rows, usually one per exchange, in one transaction. A minute can have two rows of an
    /// exchange (the app stopped and started again within it); readers add them up.
    /// </summary>
    public void Add(IReadOnlyList<MinuteStats> minutes)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var m in minutes)
            {
                Execute("""
                    INSERT INTO minute_stats VALUES ($exchange_id, $exchange, $minute, $seconds_running, $seconds_live,
                        $seconds_valid, $seconds_invalid, $book_updates, $trades, $bytes, $delay_count, $delay_p50_ms,
                        $delay_p99_ms, $delay_max_ms, $delay_histogram, $processing_p50_ns, $processing_p99_ns,
                        $hold_p50_ms, $hold_p99_ms, $catch_up_messages, $reconnects, $gaps_started, $best_bid,
                        $best_ask, $spread, $delay_negative, $delay_ahead_ms)
                    """,
                    ("exchange_id", m.ExchangeId), ("exchange", m.Exchange), ("minute", m.MinuteUtc),
                    ("seconds_running", m.SecondsRunning), ("seconds_live", m.SecondsLive),
                    ("seconds_valid", m.SecondsValid), ("seconds_invalid", m.SecondsInvalid),
                    ("book_updates", m.BookUpdates), ("trades", m.Trades), ("bytes", m.Bytes),
                    ("delay_count", m.DelayCount), ("delay_p50_ms", m.DelayP50Ms), ("delay_p99_ms", m.DelayP99Ms),
                    ("delay_max_ms", m.DelayMaxMs), ("delay_histogram", m.DelayHistogram.ToList()),
                    ("processing_p50_ns", m.ProcessingP50Ns), ("processing_p99_ns", m.ProcessingP99Ns),
                    ("hold_p50_ms", m.HoldP50Ms), ("hold_p99_ms", m.HoldP99Ms),
                    ("catch_up_messages", m.CatchUpMessages), ("reconnects", m.Reconnects),
                    ("gaps_started", m.GapsStarted), ("best_bid", m.BestBid), ("best_ask", m.BestAsk), ("spread", m.Spread),
                    ("delay_negative", m.DelayNegative), ("delay_ahead_ms", m.DelayAheadMs));
            }
            transaction.Commit();
        }
    }

    public void Add(GapRecord gap)
    {
        lock (_lock)
        {
            Execute("INSERT INTO gaps VALUES ($exchange_id, $exchange, $start_utc, $end_utc, $kind, $cause, $repair_action)",
                ("exchange_id", gap.ExchangeId), ("exchange", gap.Exchange), ("start_utc", gap.StartUtc),
                ("end_utc", gap.EndUtc), ("kind", gap.Kind.ToString()), ("cause", gap.Cause), ("repair_action", gap.RepairAction));
        }
    }

    public void Add(ReconnectRecord reconnect)
    {
        lock (_lock)
        {
            Execute("INSERT INTO reconnects VALUES ($exchange_id, $exchange, $time_utc, $reason)",
                ("exchange_id", reconnect.ExchangeId), ("exchange", reconnect.Exchange),
                ("time_utc", reconnect.TimeUtc), ("reason", reconnect.Reason));
        }
    }

    /// <summary>Deletes every minute, gap and reconnect before <paramref name="beforeUtc"/>; returns the minute rows deleted.</summary>
    public long DeleteBefore(DateTime beforeUtc)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            var minutes = Execute("DELETE FROM minute_stats WHERE minute < $before", ("before", beforeUtc));
            Execute("DELETE FROM gaps WHERE end_utc < $before", ("before", beforeUtc));
            Execute("DELETE FROM reconnects WHERE time_utc < $before", ("before", beforeUtc));
            transaction.Commit();
            return minutes;
        }
    }

    /// <summary>Runs a query under the lock and reads every row with <paramref name="read"/>.</summary>
    public List<T> Query<T>(string sql, Func<DuckDBDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        lock (_lock)
        {
            using var command = Command(sql, parameters);
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read())
            {
                rows.Add(read(reader));
            }
            return rows;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection.Dispose();
        }
    }

    private int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }

    private DuckDBCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new DuckDBParameter(name, value ?? DBNull.Value));
        }
        return command;
    }

    private void Migrate()
    {
        Execute("CREATE TABLE IF NOT EXISTS schema (version INTEGER NOT NULL)");
        var applied = Query("SELECT max(version) FROM schema", r => r.IsDBNull(0) ? 0 : r.GetInt32(0)).Single();
        for (; applied < s_migrations.Length; applied++)
        {
            using var transaction = _connection.BeginTransaction();
            Execute(s_migrations[applied]);
            Execute("INSERT INTO schema VALUES ($version)", ("version", applied + 1));
            transaction.Commit();
        }
    }
}
