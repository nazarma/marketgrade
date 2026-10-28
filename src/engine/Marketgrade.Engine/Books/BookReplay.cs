using System.Text;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine.Books;

/// <param name="Explained">A live <see cref="RecordType.BookReset"/> follows on the same connection within 5 seconds, so the live book failed there too.</param>
public sealed record ReplayFailure(long ArrivalUnixNs, long Seq, long ConnectionId, FailureKind Kind, string Cause, bool Explained);

/// <summary>Where the live book was reset, from a <see cref="RecordType.BookReset"/> record.</summary>
public sealed record LiveReset(long ArrivalUnixNs, long Seq, long ConnectionId, string Reason);

/// <summary>A Binance range the replay could not rebuild because no snapshot was recorded in time.</summary>
public sealed record MissingSnapshot(long FromUnixNs, long ToUnixNs, long Events);

/// <summary>
/// Feeds one exchange's recorded records through the same <see cref="BookHandler"/> the live book worker uses,
/// single-threaded and without repairs: a new connection or a recorded Binance snapshot rebuilds the book,
/// as live. Each failure is matched with the live book's resets.
/// </summary>
public sealed class BookReplay
{
    /// <summary>The longest a live Binance resync can wait for its snapshot: the rate limit plus the fetch timeout.</summary>
    public static readonly TimeSpan MaxSnapshotWait = TimeSpan.FromSeconds(20);

    private static readonly long s_explainNs = (long)TimeSpan.FromSeconds(5).TotalNanoseconds;

    private readonly BookHandler _handler;
    private readonly MessageBufferPool _pool = new(16, 64 * 1024);
    private readonly List<ReplayFailure> _failures = [];
    private readonly List<LiveReset> _resets = [];
    private readonly List<MissingSnapshot> _missing = [];
    private long _waitingSince = -1;
    private long _waitEvents;
    private long _lastArrival;

    public BookReplay(ExchangeAdapter adapter, PairRules rules, EngineOptions? options = null)
    {
        _handler = adapter.CreateBookHandler(rules, options ?? new EngineOptions())
            ?? throw new ArgumentException($"{adapter.Name} keeps no book", nameof(adapter));
    }

    public OrderBook Book => _handler.Book;

    public long Messages { get; private set; }

    /// <summary>Trades read; they do not touch the book.</summary>
    public long Trades { get; private set; }

    public long TradeFailures => _handler.TradeFailures;

    /// <summary>Messages applied to a valid book, including the snapshots.</summary>
    public long Applied { get; private set; }

    /// <summary>Snapshots applied and checked: the first one and every rebuild after it.</summary>
    public long Syncs { get; private set; }

    /// <summary>Recorded <see cref="RecordType.Gap"/> records: messages the recorder dropped, after which the book is rebuilt.</summary>
    public long RecordingGaps { get; private set; }

    public long FirstUnixNs { get; private set; }

    public long LastUnixNs => _lastArrival;

    public IReadOnlyList<ReplayFailure> Failures => _failures;

    public IReadOnlyList<LiveReset> LiveResets => _resets;

    public IReadOnlyList<MissingSnapshot> MissingSnapshots => _missing;

    public int UnexplainedFailures => _failures.Count(f => !f.Explained);

    /// <summary>Replays every record of <paramref name="reader"/>; files of one exchange go in order.</summary>
    public void Read(RecordingReader reader)
    {
        while (reader.Read())
        {
            var record = reader.Current;
            if (FirstUnixNs == 0)
            {
                FirstUnixNs = record.ArrivalUnixNs;
            }
            _lastArrival = record.ArrivalUnixNs;
            switch (record.Type)
            {
                case RecordType.Message:
                    Message(record, reader.Payload);
                    break;
                case RecordType.ConnectionOpened:
                    EndWait();
                    _handler.Reset();
                    break;
                case RecordType.Gap:
                    // The recorder lost messages; the book can only continue from the next snapshot.
                    RecordingGaps++;
                    EndWait();
                    _handler.Reset();
                    break;
                case RecordType.Snapshot:
                    Snapshot(record, reader.Payload);
                    break;
                case RecordType.BookReset:
                    _resets.Add(new LiveReset(record.ArrivalUnixNs, record.Seq, record.ConnectionId, Encoding.UTF8.GetString(reader.Payload)));
                    break;
            }
        }
    }

    /// <summary>Closes what is still open and matches the failures with the live resets.</summary>
    public void Finish()
    {
        EndWait();
        (_handler as BinanceBookHandler)?.ReleaseHeld();
        for (var i = 0; i < _failures.Count; i++)
        {
            var failure = _failures[i];
            var explained = _resets.Any(r => r.ConnectionId == failure.ConnectionId
                && r.Seq >= failure.Seq
                && r.ArrivalUnixNs - failure.ArrivalUnixNs <= s_explainNs);
            _failures[i] = failure with { Explained = explained };
        }
    }

    private void Message(RecordHeader record, ReadOnlySpan<byte> payload)
    {
        Messages++;
        if (_handler is BinanceBookHandler { NeedsSnapshot: true } binance)
        {
            // Live, the worker fetches now; here the recorded snapshot will follow.
            binance.StartFetch(0);
            _waitingSince = record.ArrivalUnixNs;
            _waitEvents = 0;
        }
        var buffer = Load(record, payload);
        var outcome = _handler.Handle(buffer);
        Trades += _handler.Trades;
        switch (outcome)
        {
            case HandleOutcome.Applied:
                Applied++;
                break;
            case HandleOutcome.Synced:
                Applied++;
                Synced();
                break;
            case HandleOutcome.Failed:
                _failures.Add(new ReplayFailure(record.ArrivalUnixNs, record.Seq, record.ConnectionId, _handler.Failure, _handler.Cause!, false));
                break;
            case HandleOutcome.Held:
                _waitEvents++;
                return;
        }
        buffer.Release();
    }

    private void Snapshot(RecordHeader record, ReadOnlySpan<byte> payload)
    {
        if (_handler is not BinanceBookHandler binance)
        {
            return;
        }
        if (_waitingSince < 0)
        {
            // The replayed book does not need it (it did not see what made the live book resync).
            return;
        }
        if (record.ArrivalUnixNs - _waitingSince > MaxSnapshotWait.TotalNanoseconds)
        {
            _missing.Add(new MissingSnapshot(_waitingSince, record.ArrivalUnixNs, _waitEvents));
        }
        _waitingSince = -1;
        var buffer = Load(record, payload);
        var outcome = binance.OnSnapshot(buffer);
        buffer.Release();
        Applied += binance.HeldApplied;
        if (outcome == HandleOutcome.Synced)
        {
            Synced();
        }
        else if (outcome == HandleOutcome.Failed)
        {
            _failures.Add(new ReplayFailure(record.ArrivalUnixNs, record.Seq, record.ConnectionId, binance.Failure, binance.Cause!, false));
        }
    }

    private void Synced()
    {
        Syncs++;
        _waitingSince = -1;
    }

    /// <summary>A wait for a Binance snapshot that ends without one counts as missing when it lasted too long.</summary>
    private void EndWait()
    {
        if (_waitingSince >= 0 && _lastArrival - _waitingSince > MaxSnapshotWait.TotalNanoseconds)
        {
            _missing.Add(new MissingSnapshot(_waitingSince, _lastArrival, _waitEvents));
        }
        _waitingSince = -1;
    }

    private MessageBuffer Load(RecordHeader record, ReadOnlySpan<byte> payload)
    {
        var buffer = _pool.Rent();
        while (buffer.Bytes.Length < payload.Length)
        {
            buffer.Grow(0);
        }
        payload.CopyTo(buffer.Bytes);
        buffer.Length = payload.Length;
        buffer.Seq = record.Seq;
        buffer.ConnectionId = record.ConnectionId;
        buffer.ArrivalUnixNs = record.ArrivalUnixNs;
        buffer.Counter = 1;
        return buffer;
    }
}
