using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Marketgrade.Engine.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine;

/// <summary>
/// Runs one connection per exchange and its consumers, and publishes an <see cref="EngineStatus"/>
/// once per status interval. Each consumer has its own bounded queue per exchange: the stats consumer
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
    private EngineStatus _status;
    private Task? _statusLoop;
    private Task? _workerTask;
    private Task[] _consumerTasks = [];
    private int _started;
    private Task? _stopped;

    /// <param name="recording">Recording settings; null runs the engine without recorders.</param>
    /// <param name="timeProvider">The recorders' clock; tests use a fake one.</param>
    public MarketEngine(
        EngineOptions options,
        IReadOnlyList<ExchangeAdapter> adapters,
        ILogger? logger = null,
        RecordingSettings? recording = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _events = new ConnectionEventLog(options.EventLogSize);
        Pool = new MessageBufferPool(options.PoolSize, options.BufferSize, _logger, _metrics);
        if (recording is not null)
        {
            _recording = new RecordingManager(recording, options, timeProvider ?? TimeProvider.System, _events, _logger, _metrics);
            _worker = new RecordingWorker(_recording);
        }

        _feeds = [.. adapters.Select(adapter =>
        {
            var stats = new Queue("stats", options.QueueCapacity);
            var recorder = _recording is null ? null : new Queue("recorder", options.RecorderQueueCapacity);
            var connection = new ExchangeConnection(
                adapter, Pool, [stats.Channel.Writer], options, _events, _metrics, _logger, recorder?.Channel.Writer);
            return new Feed(adapter, connection, new StatsConsumer(adapter), stats,
                _recording?.AddRecorder(adapter), recorder);
        })];

        _metrics.ObserveEngine(ObserveQueueLengths, ObserveConnectionStates);
        _status = BuildStatus(elapsedSeconds: 0, running: false);
    }

    public MessageBufferPool Pool { get; }

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

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        _consumerTasks =
        [
            .. _feeds.Select(feed => Task.Run(() => feed.Stats.RunAsync(feed.StatsQueue.Channel.Reader))),
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
            queue.Channel.Writer.TryComplete();
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
            var data = feed.Stats.DataMessages;
            var bytes = feed.Stats.Bytes;
            var lastMessage = connection.LastMessageTimestamp;
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
                feed.Stats.HeartbeatMessages,
                feed.Stats.ControlMessages,
                feed.Stats.ErrorMessages,
                bytes);
            feed.LastDataMessages = data;
            feed.LastBytes = bytes;
        }
        var recording = _recording?.BuildStatus(running, _feeds.Sum(feed => feed.Connection.RecordingDropped));
        return new EngineStatus(DateTime.UtcNow, exchanges, recording);
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

    private sealed class Queue(string name, int capacity)
    {
        public long LastWarning = LogThrottle.Never;

        public string Name { get; } = name;

        public int Capacity { get; } = capacity;

        public Channel<MessageBuffer> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<MessageBuffer>(
            new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });
    }

    private sealed class Feed(
        ExchangeAdapter adapter,
        ExchangeConnection connection,
        StatsConsumer stats,
        Queue statsQueue,
        Recorder? recorder,
        Queue? recorderQueue)
    {
        public ExchangeAdapter Adapter { get; } = adapter;

        public ExchangeConnection Connection { get; } = connection;

        public StatsConsumer Stats { get; } = stats;

        public Queue StatsQueue { get; } = statsQueue;

        public Recorder? Recorder { get; } = recorder;

        /// <summary>The connection only ever uses TryWrite on this queue, so its full mode never applies.</summary>
        public Queue? RecorderQueue { get; } = recorderQueue;

        public IEnumerable<Queue> Queues => RecorderQueue is null ? [StatsQueue] : [StatsQueue, RecorderQueue];

        public long LastDataMessages { get; set; }

        public long LastBytes { get; set; }
    }
}
