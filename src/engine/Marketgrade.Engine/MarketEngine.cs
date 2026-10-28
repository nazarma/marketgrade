using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine;

/// <summary>
/// Runs one connection per exchange and its consumers, and publishes an <see cref="EngineStatus"/>
/// once per status interval. Each consumer has its own bounded queue per exchange: the book worker
/// and, when recording is configured, the recorder.
/// </summary>
public sealed class MarketEngine : IAsyncDisposable
{
    private readonly EngineOptions _options;
    private readonly ILogger _logger;
    private readonly EngineMetrics _metrics = new();
    private readonly ConnectionEventLog _events;
    private readonly Feed[] _feeds;
    private readonly RecordingManager? _recording;
    private readonly RecordingWorker? _worker;
    private readonly CancellationTokenSource _stop = new();
    private readonly HttpClient _http;
    private readonly ClockOffset _clockOffset = new();
    private EngineStatus _status;
    private Task? _statusLoop;
    private Task? _workerTask;
    private Task[] _consumerTasks = [];
    private int _started;
    private Task? _stopped;

    /// <param name="recording">Recording settings; null runs the engine without recorders.</param>
    /// <param name="timeProvider">The recorders' clock; tests use a fake one.</param>
    /// <param name="httpHandler">Handles the REST requests (pair rules, Binance snapshots); tests stub it.</param>
    public MarketEngine(
        EngineOptions options,
        IReadOnlyList<ExchangeAdapter> adapters,
        ILogger? logger = null,
        RecordingSettings? recording = null,
        TimeProvider? timeProvider = null,
        HttpMessageHandler? httpHandler = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _http = new HttpClient(httpHandler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Marketgrade/{EngineInfo.Version}");
        _events = new ConnectionEventLog(options.EventLogSize);
        Pool = new MessageBufferPool(options.PoolSize, options.BufferSize, _logger, _metrics);
        if (recording is not null)
        {
            _recording = new RecordingManager(recording, options, timeProvider ?? TimeProvider.System, _events, _logger, _metrics);
            _worker = new RecordingWorker(_recording);
        }

        _feeds = [.. adapters.Select(adapter =>
        {
            var book = new Queue("book", options.QueueCapacity, signaled: true);
            var recorder = _recording is null ? null : new Queue("recorder", options.RecorderQueueCapacity);
            var connection = new ExchangeConnection(
                adapter, Pool, [book.Writer], options, _events, _metrics, _logger, recorder?.Writer);
            var worker = new BookWorker(adapter, connection, book.Channel.Reader, book.Signal, Pool, _http, _clockOffset,
                options, _events, _logger, _metrics, recorder?.Writer);
            return new Feed(adapter, connection, worker, book, _recording?.AddRecorder(adapter), recorder);
        })];

        _metrics.ObserveEngine(ObserveQueueLengths, ObserveConnectionStates);
        _status = BuildStatus(elapsedSeconds: 0, running: false);
    }

    public MessageBufferPool Pool { get; }

    public IEnumerable<ExchangeAdapter> Adapters => _feeds.Select(feed => feed.Adapter);

    /// <summary>The latest snapshot. Never blocks.</summary>
    public EngineStatus Status => Volatile.Read(ref _status);

    public IReadOnlyList<ConnectionEvent> GetRecentEvents(int count) => _events.GetRecent(count);

    /// <summary>Recorders pick up the new settings between batches; nothing restarts.</summary>
    public void ApplyRecordingSettings(RecordingSettings settings)
    {
        if (_recording is null)
        {
            throw new InvalidOperationException("The engine was created without recording");
        }
        _recording.Apply(settings);
    }

    /// <summary>The exchanges' public REST API for their pair rules.</summary>
    public Task<PairRules> FetchPairRulesAsync(ExchangeAdapter adapter, CancellationToken cancellationToken) =>
        PairRulesClient.FetchAsync(_http, adapter, cancellationToken);

    /// <summary>The measured offset of the local clock (local minus NTP time), or null if unknown. Corrects every delay from now on.</summary>
    public void SetClockOffset(TimeSpan? offset) => _clockOffset.Set(offset);

    /// <summary>
    /// The latest copy of an exchange's best levels (by name, any case), or null if there is none yet or no
    /// such exchange. Copies are published only while read, so after a quiet period the first one may be old.
    /// </summary>
    public BookSnapshot? GetBook(string exchange) =>
        _feeds.FirstOrDefault(feed => string.Equals(feed.Adapter.Name, exchange, StringComparison.OrdinalIgnoreCase))?.Worker.ReadSnapshot();

    public bool HasExchange(string exchange) =>
        _feeds.Any(feed => string.Equals(feed.Adapter.Name, exchange, StringComparison.OrdinalIgnoreCase));

    /// <summary>Starts without pair rules: no exchange keeps a book.</summary>
    public void Start() => Start([]);

    /// <summary>Starts every exchange; one without rules in <paramref name="rules"/> keeps no book (<see cref="BookState.NoPairRules"/>).</summary>
    public void Start(IReadOnlyList<PairRules> rules)
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        foreach (var feed in _feeds)
        {
            feed.Worker.Start(rules.FirstOrDefault(r => r.ExchangeId == feed.Adapter.Id));
        }
        _consumerTasks =
        [
            .. _feeds.Select(feed => feed.Worker.Completion),
            .. _feeds.Where(feed => feed.Recorder is not null)
                .Select(feed => Task.Run(() => feed.Recorder!.RunAsync(feed.RecorderQueue!.Channel.Reader))),
        ];
        foreach (var feed in _feeds)
        {
            feed.Connection.Start();
        }
        _statusLoop = Task.Run(StatusLoopAsync);
        if (_worker is not null)
        {
            _workerTask = Task.Run(() => _worker.RunAsync(_stop.Token));
        }
    }

    /// <summary>Stops all connections, lets the consumers drain their queues, and publishes a final status.</summary>
    public Task StopAsync()
    {
        lock (_feeds)
        {
            return _stopped ??= StopCoreAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _metrics.Dispose();
        _http.Dispose();
    }

    private async Task StopCoreAsync()
    {
        _stop.Cancel();
        if (_statusLoop is not null)
        {
            await _statusLoop;
        }
        if (_workerTask is not null)
        {
            await _workerTask;
        }
        await Task.WhenAll(_feeds.Select(feed => feed.Connection.StopAsync()));
        foreach (var queue in _feeds.SelectMany(feed => feed.Queues))
        {
            queue.Writer.TryComplete();
        }
        await Task.WhenAll(_consumerTasks);
        Volatile.Write(ref _status, BuildStatus(elapsedSeconds: 0, running: false));
    }

    private async Task StatusLoopAsync()
    {
        using var timer = new PeriodicTimer(_options.StatusInterval);
        var last = Stopwatch.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                var now = Stopwatch.GetTimestamp();
                Volatile.Write(ref _status, BuildStatus(Stopwatch.GetElapsedTime(last, now).TotalSeconds, running: true));
                last = now;
                WarnAboutFullQueues();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private EngineStatus BuildStatus(double elapsedSeconds, bool running)
    {
        var exchanges = new ExchangeStatus[_feeds.Length];
        for (var i = 0; i < _feeds.Length; i++)
        {
            var feed = _feeds[i];
            var connection = feed.Connection;
            var worker = feed.Worker;
            var data = worker.DataMessages;
            var bytes = worker.Bytes;
            var lastMessage = connection.LastMessageTimestamp;
            var priceDecimals = worker.Rules?.PriceDecimals ?? 0;
            var bid = worker.BestBid;
            var ask = worker.BestAsk;
            var delays = worker.DelayCount > 0;
            var processing = worker.ProcessingP50Ns > 0;
            exchanges[i] = new ExchangeStatus(
                feed.Adapter.Id,
                feed.Adapter.Name,
                feed.Adapter.Pair,
                connection.State,
                elapsedSeconds > 0 ? (data - feed.LastDataMessages) / elapsedSeconds : 0,
                elapsedSeconds > 0 ? (bytes - feed.LastBytes) / elapsedSeconds : 0,
                lastMessage == 0 ? null : (long)Stopwatch.GetElapsedTime(lastMessage).TotalMilliseconds,
                connection.ConnectionId,
                connection.ReconnectCount,
                connection.LastError,
                data,
                worker.HeartbeatMessages,
                worker.ControlMessages,
                worker.ErrorMessages,
                bytes,
                worker.State,
                bid == 0 ? null : ScaledDecimal.Format(bid, priceDecimals),
                ask == 0 ? null : ScaledDecimal.Format(ask, priceDecimals),
                bid == 0 || ask == 0 ? null : ScaledDecimal.Format(ask - bid, priceDecimals),
                worker.BidLevels,
                worker.AskLevels,
                worker.MaxBidLevels,
                worker.MaxAskLevels,
                delays ? worker.DelayP50Ns / 1e6 : null,
                delays ? worker.DelayP99Ns / 1e6 : null,
                processing ? worker.ProcessingP50Ns : null,
                processing ? worker.ProcessingP99Ns : null,
                worker.GapsToday,
                worker.LastGap,
                worker.QueueWaitP50Ns >= 0 ? worker.QueueWaitP50Ns : null,
                worker.QueueWaitP99Ns >= 0 ? worker.QueueWaitP99Ns : null,
                worker.CatchUpMessages,
                worker.CatchUpMessages > 0 ? worker.CatchUpMaxDelayNs / 1e6 : null,
                worker.HoldCount > 0 ? worker.HoldP50Ns / 1e6 : null,
                worker.HoldCount > 0 ? worker.HoldP99Ns / 1e6 : null,
                worker.TradeMessages);
            feed.LastDataMessages = data;
            feed.LastBytes = bytes;
        }
        var recording = _recording?.BuildStatus(running, _feeds.Sum(feed => feed.Connection.RecordingDropped + feed.Worker.RecordingDropped));
        var offset = _clockOffset.Ns;
        return new EngineStatus(DateTime.UtcNow, exchanges, recording, offset == ClockOffset.Unknown ? null : offset / 1e6);
    }

    private void WarnAboutFullQueues()
    {
        foreach (var feed in _feeds)
        {
            foreach (var queue in feed.Queues)
            {
                var length = queue.Channel.Reader.Count;
                if (length > queue.Capacity * 0.8 && LogThrottle.ShouldLog(ref queue.LastWarning))
                {
                    _logger.LogWarning(
                        "{Exchange}: {Consumer} queue is {Length} of {Capacity} full",
                        feed.Adapter.Name, queue.Name, length, queue.Capacity);
                }
            }
        }
    }

    private IEnumerable<Measurement<long>> ObserveQueueLengths() =>
        _feeds.SelectMany(feed => feed.Queues.Select(queue => new Measurement<long>(
            queue.Channel.Reader.Count,
            new TagList { { "exchange", feed.Adapter.Name }, { "consumer", queue.Name } })));

    private IEnumerable<Measurement<int>> ObserveConnectionStates() =>
        _feeds.Select(feed => new Measurement<int>(
            (int)feed.Connection.State,
            new TagList { { "exchange", feed.Adapter.Name } }));

    /// <summary>A bounded queue with one reader. The recorder's has two writers: the connection and the book worker.</summary>
    private sealed class Queue
    {
        public long LastWarning = LogThrottle.Never;

        /// <param name="signaled">Wakes a reader thread on every write, through <see cref="Signal"/>.</param>
        public Queue(string name, int capacity, bool signaled = false)
        {
            Name = name;
            Capacity = capacity;
            Channel = System.Threading.Channels.Channel.CreateBounded<MessageBuffer>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
            Writer = signaled ? new SignalingWriter(Channel.Writer, Signal) : Channel.Writer;
        }

        public string Name { get; }

        public int Capacity { get; }

        public Channel<MessageBuffer> Channel { get; }

        public ManualResetEventSlim Signal { get; } = new();

        public ChannelWriter<MessageBuffer> Writer { get; }
    }

    private sealed class Feed(
        ExchangeAdapter adapter,
        ExchangeConnection connection,
        BookWorker worker,
        Queue bookQueue,
        Recorder? recorder,
        Queue? recorderQueue)
    {
        public ExchangeAdapter Adapter { get; } = adapter;

        public ExchangeConnection Connection { get; } = connection;

        public BookWorker Worker { get; } = worker;

        public Queue BookQueue { get; } = bookQueue;

        public Recorder? Recorder { get; } = recorder;

        /// <summary>The connection and the worker only ever use TryWrite on this queue, so its full mode never applies.</summary>
        public Queue? RecorderQueue { get; } = recorderQueue;

        public IEnumerable<Queue> Queues => RecorderQueue is null ? [BookQueue] : [BookQueue, RecorderQueue];

        public long LastDataMessages { get; set; }

        public long LastBytes { get; set; }
    }
}
