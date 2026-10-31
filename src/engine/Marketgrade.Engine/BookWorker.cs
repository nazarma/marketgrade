using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Flow;
using Marketgrade.Engine.History;
using Marketgrade.Engine.Recording;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine;

/// <summary>A period in which an exchange's book was invalid, from the failed check to the repaired book.</summary>
/// <param name="StartSeq">Our sequence number of the message at which the book became invalid.</param>
/// <param name="EndSeq">Our sequence number of the last message when the book was valid again.</param>
public sealed record BookGap(DateTime StartUtc, DateTime? EndUtc, string Cause, long StartSeq, long? EndSeq)
{
    public double? DurationSeconds => EndUtc is { } end ? (end - StartUtc).TotalSeconds : null;
}

/// <summary>
/// The only live consumer of one exchange: classifies and counts every message as the stats consumer
/// did, and keeps the exchange's order book with its <see cref="BookHandler"/>. Runs on its own thread
/// and sleeps on <see cref="ManualResetEventSlim"/> until the connection writes the next message. When a
/// check fails the book becomes invalid and is repaired: Kraken and Coinbase reconnect for a new
/// snapshot, Binance fetches a new REST snapshot while the connection stays open. Per message nothing is
/// allocated: parsing, applying, the check, statistics and releasing the buffer.
/// </summary>
internal sealed class BookWorker
{
    private const int MaxBatch = 256;

    /// <summary>
    /// Messages arriving this long after a snapshot are catch-up: they queued behind the snapshot on the way
    /// (Coinbase's is 4.5 MB), so their delay is the snapshot's transfer, not the exchange's.
    /// </summary>
    public static readonly long CatchUpWindowNs = (long)TimeSpan.FromMilliseconds(100).TotalNanoseconds;

    private static readonly double s_nsPerTick = 1e9 / Stopwatch.Frequency;

    // The catch-up window on the monotonic clock, so a step of the wall clock cannot move a message in or out of it.
    private static readonly long s_catchUpWindowTicks = (long)(CatchUpWindowNs / s_nsPerTick);

    private readonly ExchangeAdapter _adapter;
    private readonly ExchangeConnection _connection;
    private readonly ChannelReader<MessageBuffer> _reader;
    private readonly ManualResetEventSlim _signal;
    private readonly ChannelWriter<MessageBuffer>? _recorder;
    private readonly MessageBufferPool _pool;
    private readonly HttpClient _http;
    private readonly NtpClock _ntpClock;
    private readonly EngineOptions _options;
    private readonly ConnectionEventLog _events;
    private readonly EngineMetrics? _metrics;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WindowedHistogram _delay;
    /// <summary>How far ahead the negative delays were (their magnitude).</summary>
    private readonly WindowedHistogram _ahead;
    private readonly WindowedHistogram _processing;
    private readonly WindowedHistogram _queueWait;
    private readonly WindowedHistogram _hold;
    private readonly CancellationTokenSource _stop = new();
    private readonly FlowRing _flow = new();
    private readonly MinuteRecorder _minutes;
    private readonly HistoryFeed? _history;

    private BookHandler? _handler;
    private Thread? _thread;
    private long _lastSeq;
    private long _lastConnectionId;
    private long _nextStatistics;
    private long _lastRecordingWarning = LogThrottle.Never;
    private BookGap? _openGap;
    private GapKind _openGapKind;
    private DateOnly _gapDay;
    private long _version;
    private long _publishedVersion = -1;
    private long _nextPublish;

    // Written by this worker, read by the status loop.
    private long _dataMessages;
    private long _heartbeatMessages;
    private long _tradeMessages;
    private long _trades;
    private long _bookChanges;
    private long _tradeFailures;
    private bool _flowEnabled;
    private long _controlMessages;
    private long _errorMessages;
    private long _bytes;
    private long _recordingDropped;
    private int _state = (int)BookState.NoPairRules;
    private long _bestBid;
    private long _bestAsk;
    private int _bidLevels;
    private int _askLevels;
    private int _maxBidLevels;
    private int _maxAskLevels;
    private long _delayP50;
    private long _delayP99;
    private long _delayCount;
    private long _delayNegative;
    private long _delaySkipped;
    private long _delayAheadP99;
    private long _processingP50;
    private long _processingP99;
    private long _catchUpStart = long.MinValue;
    private long _catchUpMessages;
    private long _catchUpMaxDelay;
    private long _holdP50;
    private long _holdP99;
    private long _holdCount;
    private long _queueWaitP50 = -1;
    private long _queueWaitP99 = -1;
    private int _gapsToday;
    private BookGap? _lastGap;
    private long _lastRead = long.MinValue;
    private BookSnapshot? _snapshot;

    // Handed over by the snapshot fetch task.
    private Task? _fetch;
    private FetchResult? _fetchResult;

    public BookWorker(
        ExchangeAdapter adapter,
        ExchangeConnection connection,
        ChannelReader<MessageBuffer> reader,
        ManualResetEventSlim signal,
        MessageBufferPool pool,
        HttpClient http,
        NtpClock ntpClock,
        EngineOptions options,
        ConnectionEventLog events,
        ILogger logger,
        EngineMetrics? metrics = null,
        ChannelWriter<MessageBuffer>? recorder = null,
        TimeProvider? time = null,
        HistoryFeed? history = null)
    {
        _adapter = adapter;
        _connection = connection;
        _reader = reader;
        _signal = signal;
        _pool = pool;
        _http = http;
        _ntpClock = ntpClock;
        _options = options;
        _events = events;
        _logger = logger;
        _metrics = metrics;
        _recorder = recorder;
        _time = time ?? TimeProvider.System;
        var now = _time.GetTimestamp();
        _delay = new WindowedHistogram(6, _time.TimestampFrequency * 10, now);
        _ahead = new WindowedHistogram(6, _time.TimestampFrequency * 10, now);
        _processing = new WindowedHistogram(6, _time.TimestampFrequency * 10, now);
        _queueWait = new WindowedHistogram(6, _time.TimestampFrequency * 10, now);
        _hold = new WindowedHistogram(6, _time.TimestampFrequency * 10, now);
        _minutes = new MinuteRecorder(adapter.Id, adapter.Name, (long)options.AsleepAfter.TotalNanoseconds);
        _history = history;
    }

    public ExchangeAdapter Adapter => _adapter;

    public PairRules? Rules => _handler?.Book.Rules;

    /// <summary>Completes once the queue is completed and drained and every held buffer is released.</summary>
    public Task Completion => _done.Task;

    public long DataMessages => Interlocked.Read(ref _dataMessages);

    public long HeartbeatMessages => Interlocked.Read(ref _heartbeatMessages);

    public long TradeMessages => Interlocked.Read(ref _tradeMessages);

    /// <summary>Trades parsed; a message can hold several.</summary>
    public long Trades => Interlocked.Read(ref _trades);

    /// <summary>Trade messages that could not be read; they never affect the book.</summary>
    public long TradeFailures => Interlocked.Read(ref _tradeFailures);

    /// <summary>Levels changed by updates to a valid book, at any distance from the mid price.</summary>
    public long BookChanges => Interlocked.Read(ref _bookChanges);

    /// <summary>The trades and the book changes near the mid price of this exchange, while <see cref="FlowEnabled"/>.</summary>
    public FlowRing Flow => _flow;

    /// <summary>Writes to <see cref="Flow"/> start or stop with the next batch of messages; counting goes on either way.</summary>
    public bool FlowEnabled
    {
        get => Volatile.Read(ref _flowEnabled);
        set => Volatile.Write(ref _flowEnabled, value);
    }

    public long ControlMessages => Interlocked.Read(ref _controlMessages);

    public long ErrorMessages => Interlocked.Read(ref _errorMessages);

    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Snapshot and book reset records dropped because the recorder's queue was full.</summary>
    public long RecordingDropped => Interlocked.Read(ref _recordingDropped);

    public BookState State => (BookState)Volatile.Read(ref _state);

    /// <summary>Best bid and ask, scaled by the pair's decimals; 0 when that side is empty or the book is not valid.</summary>
    public long BestBid => Interlocked.Read(ref _bestBid);

    public long BestAsk => Interlocked.Read(ref _bestAsk);

    public int BidLevels => Volatile.Read(ref _bidLevels);

    public int AskLevels => Volatile.Read(ref _askLevels);

    public int MaxBidLevels => Volatile.Read(ref _maxBidLevels);

    public int MaxAskLevels => Volatile.Read(ref _maxAskLevels);

    /// <summary>Delay percentiles over the last minute in nanoseconds; 0 without data. On NTP time once the clock is synced.</summary>
    public long DelayP50Ns => Interlocked.Read(ref _delayP50);

    public long DelayP99Ns => Interlocked.Read(ref _delayP99);

    /// <summary>Delays measured in the last minute.</summary>
    public long DelayCount => Interlocked.Read(ref _delayCount);

    /// <summary>Of those, the delays below zero (counted as zero), see <see cref="NegativeDelays"/>.</summary>
    public long DelayNegative => Interlocked.Read(ref _delayNegative);

    /// <summary>Delays not measured since the start because the clock was being synced after a step or a wake from sleep.</summary>
    public long DelaySkipped => Interlocked.Read(ref _delaySkipped);

    /// <summary>How far below zero the negative delays went, 99th percentile, in nanoseconds; 0 without any.</summary>
    public long DelayAheadP99Ns => Interlocked.Read(ref _delayAheadP99);

    public long ProcessingP50Ns => Interlocked.Read(ref _processingP50);

    public long ProcessingP99Ns => Interlocked.Read(ref _processingP99);

    /// <summary>Messages since the last snapshot that arrived within <see cref="CatchUpWindowNs"/> after it; not in the delay statistics.</summary>
    public long CatchUpMessages => Interlocked.Read(ref _catchUpMessages);

    /// <summary>The largest delay of those catch-up messages, in nanoseconds.</summary>
    public long CatchUpMaxDelayNs => Interlocked.Read(ref _catchUpMaxDelay);

    /// <summary>Exchange hold time percentiles over the last minute in nanoseconds (Coinbase only, see <see cref="BookHandler.ExchangeHoldNs"/>).</summary>
    public long HoldP50Ns => Interlocked.Read(ref _holdP50);

    public long HoldP99Ns => Interlocked.Read(ref _holdP99);

    /// <summary>Hold times measured in the last minute; 0 for an exchange that does not report change times.</summary>
    public long HoldCount => Interlocked.Read(ref _holdCount);

    /// <summary>Time from receiving a message to the worker starting on it, over the last minute; 0 without data.</summary>
    public long QueueWaitP50Ns => Interlocked.Read(ref _queueWaitP50);

    public long QueueWaitP99Ns => Interlocked.Read(ref _queueWaitP99);

    /// <summary>Gaps that started today (UTC).</summary>
    public int GapsToday => Volatile.Read(ref _gapsToday);

    public BookGap? LastGap => Volatile.Read(ref _lastGap);

    /// <summary>
    /// The latest published copy of the best levels, or null before the first one. Reading it keeps the worker
    /// publishing for the next 5 seconds, so the first read after a quiet period may get an older copy.
    /// </summary>
    public BookSnapshot? ReadSnapshot()
    {
        Interlocked.Exchange(ref _lastRead, _time.GetTimestamp());
        return Volatile.Read(ref _snapshot);
    }

    /// <summary>The book's handler, for benchmarks that drive <see cref="Process"/> themselves.</summary>
    internal BookHandler? Handler => _handler;

    /// <summary>The current minute's statistics, before they are published (tests).</summary>
    internal MinuteRecorder Minutes => _minutes;

    /// <summary>Starts the worker thread; without rules the exchange keeps no book and only counts messages.</summary>
    public void Start(PairRules? rules)
    {
        Prepare(rules);
        _thread = new Thread(Run) { IsBackground = true, Name = $"book-{_adapter.Name.ToLowerInvariant()}" };
        _thread.Start();
    }

    /// <summary>Creates the book's handler without starting the thread.</summary>
    internal void Prepare(PairRules? rules)
    {
        if (rules is not null)
        {
            _handler = _adapter.CreateBookHandler(rules, _options, _logger);
        }
        Volatile.Write(ref _state, (int)(_handler is null ? BookState.NoPairRules : BookState.Syncing));
        UpdateFlow();
    }

    private void UpdateFlow()
    {
        if (_handler is not null)
        {
            _handler.Flow = FlowEnabled ? _flow : null;
        }
    }

    private void Run()
    {
        try
        {
            var wait = _options.BookHousekeepingInterval;
            while (true)
            {
                _signal.Reset();
                for (var i = 0; i < MaxBatch && _reader.TryRead(out var buffer); i++)
                {
                    Process(buffer);
                }
                Housekeeping();
                if (_reader.Completion.IsCompleted)
                {
                    break;
                }
                if (_reader.Count == 0)
                {
                    _signal.Wait(wait);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, "{Exchange}: book worker failed", _adapter.Name);
            while (_reader.TryRead(out var buffer))
            {
                buffer.Release();
            }
        }
        finally
        {
            StopFetch();
            (_handler as BinanceBookHandler)?.ReleaseHeld();
            FlushHistory();
            _done.TrySetResult();
        }
    }

    internal void Process(MessageBuffer buffer)
    {
        Debug.Assert(buffer.Length > 0, "Buffer was released before this consumer read it");
        Debug.Assert(buffer.Type == RecordType.Message, "Connection records go to the recorder only");
        var start = Stopwatch.GetTimestamp();
        _queueWait.Record((long)((start - buffer.ReceivedTimestamp) * s_nsPerTick));
        var outcome = HandleOutcome.Ignored;
        MessageKind kind;
        if (_handler is null)
        {
            kind = _adapter.Classify(buffer.Span);
        }
        else
        {
            if (buffer.ConnectionId != _lastConnectionId)
            {
                ConnectionChanged(buffer.Seq);
                _lastConnectionId = buffer.ConnectionId;
            }
            _lastSeq = buffer.Seq;
            outcome = _handler.Handle(buffer);
            kind = _handler.Kind;
            if (_handler.BookChanges > 0)
            {
                Interlocked.Add(ref _bookChanges, _handler.BookChanges);
            }
            if (outcome is HandleOutcome.Applied or HandleOutcome.Synced or HandleOutcome.Failed)
            {
                _version++;
            }
            if (outcome == HandleOutcome.Synced)
            {
                if (_handler is not BinanceBookHandler)
                {
                    // Kraken and Coinbase: this message is the snapshot.
                    StartCatchUp(buffer.ReceivedTimestamp);
                }
                Repaired();
            }
            else if (outcome == HandleOutcome.Failed)
            {
                Invalid(_handler.Cause!, KindOf(_handler.Failure), buffer.Seq, buffer.ConnectionId, reconnect: _handler is not BinanceBookHandler);
            }
            var exchangeTime = _handler.ExchangeUnixNs;
            if (exchangeTime != 0)
            {
                // NTP time at arrival from the monotonic clock; the wall clock until the first sync.
                switch (_ntpClock.Read(buffer.ReceivedTimestamp, out var ntp))
                {
                    case ClockReading.Known:
                        RecordDelay(buffer.ReceivedTimestamp, ntp - exchangeTime);
                        break;
                    case ClockReading.Held:
                        Interlocked.Increment(ref _delaySkipped);
                        break;
                    default:
                        RecordDelay(buffer.ReceivedTimestamp, buffer.ArrivalUnixNs - exchangeTime);
                        break;
                }
            }
            if (_handler.ExchangeHoldNs != BookHandler.NoHold)
            {
                _hold.Record(_handler.ExchangeHoldNs);
                _minutes.Hold.Record(_handler.ExchangeHoldNs);
            }
            PublishTop();
        }

        switch (kind)
        {
            case MessageKind.Data:
                Interlocked.Increment(ref _dataMessages);
                _minutes.BookUpdates++;
                break;
            case MessageKind.Trade:
                Interlocked.Increment(ref _tradeMessages);
                if (_handler is not null)
                {
                    Interlocked.Add(ref _trades, _handler.Trades);
                    _minutes.Trades += _handler.Trades;
                    Interlocked.Exchange(ref _tradeFailures, _handler.TradeFailures);
                }
                break;
            case MessageKind.Heartbeat:
                Interlocked.Increment(ref _heartbeatMessages);
                break;
            case MessageKind.Control:
                Interlocked.Increment(ref _controlMessages);
                break;
            default:
                Interlocked.Increment(ref _errorMessages);
                break;
        }
        Interlocked.Add(ref _bytes, buffer.Length);
        _minutes.Bytes += buffer.Length;
        if (_handler is not null)
        {
            var processing = (long)((Stopwatch.GetTimestamp() - start) * s_nsPerTick);
            _processing.Record(processing);
            _minutes.Processing.Record(processing);
        }
        if (outcome != HandleOutcome.Held)
        {
            buffer.Release();
        }
    }

    /// <summary>A delay goes to the statistics, or to the catch-up counters if the message arrived right after a snapshot.</summary>
    private void RecordDelay(long arrivalTimestamp, long delayNs)
    {
        if (_catchUpStart != long.MinValue)
        {
            var sinceSnapshot = arrivalTimestamp - _catchUpStart;
            if (sinceSnapshot >= 0 && sinceSnapshot <= s_catchUpWindowTicks)
            {
                Interlocked.Increment(ref _catchUpMessages);
                _minutes.CatchUp++;
                if (delayNs > Interlocked.Read(ref _catchUpMaxDelay))
                {
                    Interlocked.Exchange(ref _catchUpMaxDelay, delayNs);
                }
                return;
            }
        }
        _delay.Record(delayNs);
        _minutes.Delay.Record(delayNs);
        if (delayNs < 0)
        {
            _ahead.Record(-delayNs);
            _minutes.Ahead.Record(-delayNs);
        }
    }

    /// <summary>Opens the catch-up window at a snapshot's <see cref="Stopwatch"/> timestamp; the counters start again.</summary>
    private void StartCatchUp(long snapshotTimestamp)
    {
        _catchUpStart = snapshotTimestamp;
        Interlocked.Exchange(ref _catchUpMessages, 0);
        Interlocked.Exchange(ref _catchUpMaxDelay, 0);
    }

    /// <summary>Runs after each batch and at least once per housekeeping interval. Allocation-free unless something changes.</summary>
    private void Housekeeping()
    {
        var now = _time.GetTimestamp();
        if (now >= _nextStatistics)
        {
            _nextStatistics = now + _time.TimestampFrequency;
            PublishStatistics(now);
        }
        if (_minutes.Advance(UtcNowUnixNs(), _connection.State == ConnectionState.Live, State,
                _connection.LiveConnectionsEnded, BestBid, BestAsk, Rules) is { } minute)
        {
            _history?.Add(minute);
        }
        if (_handler is null)
        {
            return;
        }
        UpdateFlow();

        // A book can only be as current as its connection. Stopping the engine is no gap.
        var connection = _connection.State;
        if (_handler.Book.State == BookState.Valid
            && connection != ConnectionState.Stopped
            && (connection != ConnectionState.Live || _connection.ConnectionId != _lastConnectionId))
        {
            _handler.Invalidate();
            Invalid(LostCause(), GapKind.ConnectionLost, _lastSeq, _lastConnectionId, reconnect: false);
            PublishTop();
        }

        if (_handler is BinanceBookHandler binance)
        {
            SyncBinance(binance, now);
        }
        PublishSnapshot(now);
    }

    /// <summary>At most 10 times per second, only while read in the last 5 seconds and only if the book changed.</summary>
    private void PublishSnapshot(long now)
    {
        var lastRead = Interlocked.Read(ref _lastRead);
        if (lastRead == long.MinValue
            || now - lastRead > _time.TimestampFrequency * 5
            || now < _nextPublish
            || _version == _publishedVersion)
        {
            return;
        }
        _nextPublish = now + _time.TimestampFrequency / 10;
        _publishedVersion = _version;
        Volatile.Write(ref _snapshot, BookSnapshot.From(_handler!.Book, _adapter.Name, _adapter.Pair, _time.GetUtcNow().UtcDateTime));
    }

    /// <summary>The first message of a new connection: a valid book has missed whatever happened in between.</summary>
    private void ConnectionChanged(long seq)
    {
        if (_lastConnectionId != 0 && _handler!.Book.State == BookState.Valid)
        {
            _handler.Invalidate();
            Invalid(LostCause(), GapKind.ConnectionLost, seq, _lastConnectionId, reconnect: false);
        }
    }

    private string LostCause() =>
        _connection.State == ConnectionState.Live
            ? "New connection"
            : $"Connection lost: {_connection.LastError ?? _connection.State.ToString()}";

    private void Invalid(string cause, GapKind kind, long seq, long connectionId, bool reconnect)
    {
        Volatile.Write(ref _state, (int)BookState.Invalid);
        _version++;
        var now = _time.GetUtcNow().UtcDateTime;
        if (_openGap is null)
        {
            var day = DateOnly.FromDateTime(now);
            if (day != _gapDay)
            {
                _gapDay = day;
                Volatile.Write(ref _gapsToday, 0);
            }
            Interlocked.Increment(ref _gapsToday);
            _minutes.GapsStarted++;
            _openGap = new BookGap(now, null, cause, seq, null);
            _openGapKind = kind;
            Volatile.Write(ref _lastGap, _openGap);
        }
        _logger.LogWarning("{Exchange}: book invalid at message {Seq}: {Cause}", _adapter.Name, seq, cause);
        _events.Add(_adapter.Name, "Book invalid", cause);
        RecordText(RecordType.BookReset, cause, connectionId);
        if (reconnect)
        {
            _connection.RequestReconnect(connectionId, cause);
        }
    }

    private void Repaired()
    {
        Volatile.Write(ref _state, (int)BookState.Valid);
        _version++;
        if (_openGap is { } gap)
        {
            var closed = gap with { EndUtc = _time.GetUtcNow().UtcDateTime, EndSeq = _lastSeq };
            _openGap = null;
            Volatile.Write(ref _lastGap, closed);
            _history?.Add(new GapRecord(_adapter.Id, _adapter.Name, gap.StartUtc, closed.EndUtc.Value, _openGapKind, gap.Cause, RepairAction(_openGapKind)));
            _logger.LogInformation("{Exchange}: book valid again after {Seconds:0.0} s ({Cause})", _adapter.Name, closed.DurationSeconds, gap.Cause);
            _events.Add(_adapter.Name, "Book valid", $"gap of {closed.DurationSeconds:0.0} s: {gap.Cause}");
        }
        else
        {
            _logger.LogInformation("{Exchange}: book synced", _adapter.Name);
            _events.Add(_adapter.Name, "Book synced");
        }
    }

    private static GapKind KindOf(FailureKind failure) => failure switch
    {
        FailureKind.Checksum => GapKind.Checksum,
        FailureKind.Sequence => GapKind.Sequence,
        _ => GapKind.Parse,
    };

    private string RepairAction(GapKind kind) => (_handler is BinanceBookHandler, kind) switch
    {
        (true, GapKind.ConnectionLost) => "Reconnected and rebuilt the book from a new REST snapshot",
        (true, _) => "Rebuilt the book from a new REST snapshot on the same connection",
        _ => "Reconnected and rebuilt the book from a new snapshot",
    };

    /// <summary>As the worker stops: the minute so far, and a gap still open, which ends here.</summary>
    private void FlushHistory()
    {
        if (_minutes.Flush(UtcNowUnixNs(), _connection.State == ConnectionState.Live, State,
                _connection.LiveConnectionsEnded, BestBid, BestAsk, Rules) is { } minute)
        {
            _history?.Add(minute);
        }
        if (_openGap is { } gap)
        {
            _history?.Add(new GapRecord(_adapter.Id, _adapter.Name, gap.StartUtc, _time.GetUtcNow().UtcDateTime, _openGapKind, gap.Cause,
                "The app was closed before the book was repaired"));
        }
    }

    private long UtcNowUnixNs() => (_time.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks) * 100;

    private void PublishTop()
    {
        var book = _handler!.Book;
        var valid = book.State == BookState.Valid;
        Interlocked.Exchange(ref _bestBid, valid && book.Bids.Count > 0 ? book.Bids.PriceAt(0) : 0);
        Interlocked.Exchange(ref _bestAsk, valid && book.Asks.Count > 0 ? book.Asks.PriceAt(0) : 0);
        Volatile.Write(ref _bidLevels, book.Bids.Count);
        Volatile.Write(ref _askLevels, book.Asks.Count);
        Volatile.Write(ref _maxBidLevels, book.Bids.MaxCount);
        Volatile.Write(ref _maxAskLevels, book.Asks.MaxCount);
    }

    internal void PublishStatistics(long now)
    {
        _delay.Rotate(now);
        _ahead.Rotate(now);
        _processing.Rotate(now);
        _queueWait.Rotate(now);
        _hold.Rotate(now);
        var delay = _delay.Merge();
        Interlocked.Exchange(ref _delayP50, delay.Percentile(50));
        Interlocked.Exchange(ref _delayP99, delay.Percentile(99));
        Interlocked.Exchange(ref _delayCount, delay.Count);
        var ahead = _ahead.Merge();
        Interlocked.Exchange(ref _delayNegative, ahead.Count);
        Interlocked.Exchange(ref _delayAheadP99, ahead.Percentile(99));
        var processing = _processing.Merge();
        Interlocked.Exchange(ref _processingP50, processing.Percentile(50));
        Interlocked.Exchange(ref _processingP99, processing.Percentile(99));
        var hold = _hold.Merge();
        Interlocked.Exchange(ref _holdP50, hold.Percentile(50));
        Interlocked.Exchange(ref _holdP99, hold.Percentile(99));
        Interlocked.Exchange(ref _holdCount, hold.Count);
        var queueWait = _queueWait.Merge();
        Interlocked.Exchange(ref _queueWaitP50, queueWait.Count > 0 ? queueWait.Percentile(50) : -1);
        Interlocked.Exchange(ref _queueWaitP99, queueWait.Count > 0 ? queueWait.Percentile(99) : -1);
        if (_gapDay != default && DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime) != _gapDay)
        {
            _gapDay = default;
            Volatile.Write(ref _gapsToday, 0);
        }
    }

    private void SyncBinance(BinanceBookHandler binance, long now)
    {
        var nowMs = now * 1000 / _time.TimestampFrequency;
        if (Interlocked.Exchange(ref _fetchResult, null) is { } result)
        {
            if (result.Snapshot is { } snapshot)
            {
                snapshot.Seq = _lastSeq;
                snapshot.ConnectionId = _lastConnectionId;
                QueueForRecorder(snapshot);
                var outcome = binance.OnSnapshot(snapshot);
                snapshot.Release();
                if (outcome != HandleOutcome.Failed)
                {
                    // Events that arrive from now on may have waited for the snapshot to be applied.
                    StartCatchUp(Stopwatch.GetTimestamp());
                }
                if (outcome == HandleOutcome.Synced)
                {
                    Repaired();
                }
                else if (outcome == HandleOutcome.Failed)
                {
                    Invalid(binance.Cause!, KindOf(binance.Failure), _lastSeq, _lastConnectionId, reconnect: false);
                }
                PublishTop();
            }
            else
            {
                binance.FetchFailed(result.RetryAtMs);
            }
        }

        if (_fetch is { IsCompleted: false }
            || _lastConnectionId == 0
            || _connection.State != ConnectionState.Live
            || _connection.ConnectionId != _lastConnectionId
            || !binance.ShouldFetch(nowMs))
        {
            return;
        }
        binance.StartFetch(nowMs);
        Volatile.Write(ref _state, (int)binance.Book.State);
        var adapter = (BinanceAdapter)_adapter;
        _fetch = Task.Run(() => FetchSnapshotAsync(adapter.DepthSnapshotUri, nowMs));
    }

    /// <summary>Fetches a depth snapshot into a pooled buffer and hands it to the worker thread.</summary>
    private async Task FetchSnapshotAsync(Uri uri, long startedMs)
    {
        MessageBuffer? buffer = null;
        var delivered = false;
        var retryAtMs = startedMs;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(_options.BinanceSnapshotTimeout);
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is 429 or 418)
            {
                var wait = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? (TimeSpan?)(date - _time.GetUtcNow()) : null)
                    ?? TimeSpan.FromMinutes(1);
                retryAtMs = startedMs + (long)Math.Max(0, wait.TotalMilliseconds);
                _logger.LogWarning("{Exchange}: depth snapshot refused with HTTP {Status}, backing off for {Seconds:0} s",
                    _adapter.Name, (int)response.StatusCode, wait.TotalSeconds);
                _events.Add(_adapter.Name, "Snapshot rate limited", $"HTTP {(int)response.StatusCode}, retry in {wait.TotalSeconds:0} s");
                return;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("{Exchange}: depth snapshot failed with HTTP {Status}", _adapter.Name, (int)response.StatusCode);
                return;
            }

            buffer = _pool.Rent();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var received = 0;
            while (true)
            {
                if (received == buffer.Bytes.Length)
                {
                    if (received >= _options.MaxMessageBytes)
                    {
                        throw new InvalidDataException($"Snapshot larger than {_options.MaxMessageBytes} bytes");
                    }
                    buffer.Grow(received);
                }
                var read = await stream.ReadAsync(buffer.Bytes.AsMemory(received), timeout.Token);
                if (read == 0)
                {
                    break;
                }
                received += read;
            }
            buffer.Length = received;
            buffer.Type = RecordType.Snapshot;
            buffer.ExchangeId = _adapter.Id;
            buffer.ArrivalUnixNs = (_time.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
            buffer.Counter = _recorder is null ? 1 : 2;
            Volatile.Write(ref _fetchResult, new FetchResult(buffer, 0));
            buffer = null;
            delivered = true;
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or InvalidDataException)
        {
            _logger.LogWarning("{Exchange}: depth snapshot failed: {Error}", _adapter.Name, e.Message);
        }
        finally
        {
            if (buffer is not null)
            {
                buffer.Counter = 1;
                buffer.Release();
            }
            if (!_stop.IsCancellationRequested)
            {
                if (!delivered)
                {
                    Volatile.Write(ref _fetchResult, new FetchResult(null, retryAtMs));
                }
                _signal.Set();
            }
        }
    }

    /// <summary>Cancels a running fetch, waits for it and releases a snapshot it handed over.</summary>
    private void StopFetch()
    {
        _stop.Cancel();
        try
        {
            _fetch?.Wait();
        }
        catch (AggregateException)
        {
        }
        if (Interlocked.Exchange(ref _fetchResult, null)?.Snapshot is { } snapshot)
        {
            snapshot.Counter = 1;
            snapshot.Release();
        }
    }

    private void RecordText(RecordType type, string text, long connectionId)
    {
        if (_recorder is null)
        {
            return;
        }
        var buffer = _pool.Rent();
        buffer.Length = Encoding.UTF8.GetBytes(text.AsSpan(0, Math.Min(text.Length, buffer.Bytes.Length / 3)), buffer.Bytes);
        buffer.Type = type;
        buffer.ExchangeId = _adapter.Id;
        buffer.ConnectionId = connectionId;
        buffer.Seq = _lastSeq;
        buffer.ArrivalUnixNs = (_time.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
        buffer.Counter = 2;
        QueueForRecorder(buffer);
        buffer.Release();
    }

    /// <summary>
    /// Hands the recorder its reference to <paramref name="buffer"/> without waiting. If its queue is full, the
    /// recorder's reference is released here, counted as dropped and logged; the caller keeps its own.
    /// </summary>
    private void QueueForRecorder(MessageBuffer buffer)
    {
        if (_recorder is null || _recorder.TryWrite(buffer))
        {
            return;
        }
        buffer.Release();
        Interlocked.Increment(ref _recordingDropped);
        _metrics?.RecordingDropped.Add(1, new KeyValuePair<string, object?>("exchange", _adapter.Name));
        if (LogThrottle.ShouldLog(ref _lastRecordingWarning))
        {
            _logger.LogWarning("{Exchange}: recorder queue full, a {Type} record was not recorded", _adapter.Name, buffer.Type);
        }
    }

    private sealed record FetchResult(MessageBuffer? Snapshot, long RetryAtMs);
}
