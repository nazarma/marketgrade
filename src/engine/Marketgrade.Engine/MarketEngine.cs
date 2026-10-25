using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine;

/// <summary>
/// Runs one connection per exchange and its consumers, and publishes an <see cref="EngineStatus"/>
/// once per status interval. Each consumer has its own bounded queue per exchange.
/// </summary>
public sealed class MarketEngine : IAsyncDisposable
{
    private readonly EngineOptions _options;
    private readonly ILogger _logger;
    private readonly EngineMetrics _metrics = new();
    private readonly ConnectionEventLog _events;
    private readonly Feed[] _feeds;
    private readonly CancellationTokenSource _stop = new();
    private EngineStatus _status;
    private Task? _statusLoop;
    private Task[] _consumerTasks = [];
    private int _started;
    private Task? _stopped;

    public MarketEngine(EngineOptions options, IReadOnlyList<ExchangeAdapter> adapters, ILogger? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger.Instance;
        _events = new ConnectionEventLog(options.EventLogSize);
        Pool = new MessageBufferPool(options.PoolSize, options.BufferSize, _logger, _metrics);

        var queueOptions = new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        };
        _feeds = [.. adapters.Select(adapter =>
        {
            // Step 3 adds the recorder here as a second queue and consumer.
            var stats = new Queue("stats", Channel.CreateBounded<MessageBuffer>(queueOptions));
            var connection = new ExchangeConnection(adapter, Pool, [stats.Channel.Writer], options, _events, _metrics, _logger);
            return new Feed(adapter, connection, new StatsConsumer(adapter), stats);
        })];

        _metrics.ObserveEngine(ObserveQueueLengths, ObserveConnectionStates);
        _status = BuildStatus(elapsedSeconds: 0);
    }

    public MessageBufferPool Pool { get; }

    /// <summary>The latest snapshot. Never blocks.</summary>
    public EngineStatus Status => Volatile.Read(ref _status);

    public IReadOnlyList<ConnectionEvent> GetRecentEvents(int count) => _events.GetRecent(count);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }
        _consumerTasks = [.. _feeds.Select(feed => Task.Run(() => feed.Stats.RunAsync(feed.StatsQueue.Channel.Reader)))];
        foreach (var feed in _feeds)
        {
            feed.Connection.Start();
        }
        _statusLoop = Task.Run(StatusLoopAsync);
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
        await Task.WhenAll(_feeds.Select(feed => feed.Connection.StopAsync()));
        foreach (var feed in _feeds)
        {
            feed.StatsQueue.Channel.Writer.TryComplete();
        }
        await Task.WhenAll(_consumerTasks);
        Volatile.Write(ref _status, BuildStatus(elapsedSeconds: 0));
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
                Volatile.Write(ref _status, BuildStatus(Stopwatch.GetElapsedTime(last, now).TotalSeconds));
                last = now;
                WarnAboutFullQueues();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private EngineStatus BuildStatus(double elapsedSeconds)
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
        return new EngineStatus(DateTime.UtcNow, exchanges);
    }

    private void WarnAboutFullQueues()
    {
        foreach (var feed in _feeds)
        {
            var queue = feed.StatsQueue;
            var length = queue.Channel.Reader.Count;
            if (length > _options.QueueCapacity * 0.8 && LogThrottle.ShouldLog(ref queue.LastWarning))
            {
                _logger.LogWarning(
                    "{Exchange}: {Consumer} queue is {Length} of {Capacity} full",
                    feed.Adapter.Name, queue.Name, length, _options.QueueCapacity);
            }
        }
    }

    private IEnumerable<Measurement<long>> ObserveQueueLengths() =>
        _feeds.Select(feed => new Measurement<long>(
            feed.StatsQueue.Channel.Reader.Count,
            new TagList { { "exchange", feed.Adapter.Name }, { "consumer", feed.StatsQueue.Name } }));

    private IEnumerable<Measurement<int>> ObserveConnectionStates() =>
        _feeds.Select(feed => new Measurement<int>(
            (int)feed.Connection.State,
            new TagList { { "exchange", feed.Adapter.Name } }));

    private sealed class Queue(string name, Channel<MessageBuffer> channel)
    {
        public long LastWarning = LogThrottle.Never;

        public string Name { get; } = name;

        public Channel<MessageBuffer> Channel { get; } = channel;
    }

    private sealed class Feed(ExchangeAdapter adapter, ExchangeConnection connection, StatsConsumer stats, Queue statsQueue)
    {
        public ExchangeAdapter Adapter { get; } = adapter;

        public ExchangeConnection Connection { get; } = connection;

        public StatsConsumer Stats { get; } = stats;

        public Queue StatsQueue { get; } = statsQueue;

        public long LastDataMessages { get; set; }

        public long LastBytes { get; set; }
    }
}
