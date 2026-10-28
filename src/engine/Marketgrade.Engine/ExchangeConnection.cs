using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Marketgrade.Engine.History;
using Marketgrade.Engine.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine;

/// <summary>
/// Keeps one WebSocket connection to an exchange alive: connects, subscribes, receives every
/// message into a pooled buffer, hands it to each consumer queue, and reconnects when needed.
/// The connection waits for a full consumer queue, except the recorder's: what does not fit there
/// is dropped for the recorder only, so recording never slows live monitoring.
/// </summary>
public sealed class ExchangeConnection
{
    private readonly ExchangeAdapter _adapter;
    private readonly MessageBufferPool _pool;
    private readonly ChannelWriter<MessageBuffer>[] _writers;
    private readonly ChannelWriter<MessageBuffer>? _recorder;
    private readonly EngineOptions _options;
    private readonly ConnectionEventLog _events;
    private readonly EngineMetrics? _metrics;
    private readonly ILogger _logger;
    private readonly HistoryFeed? _history;
    private readonly TagList _exchangeTag;
    private readonly CancellationTokenSource _stop = new();

    private int _state = (int)ConnectionState.Stopped;
    private long _connectionId;
    private long _seq;
    private long _lastMessageTimestamp;
    private int _reconnectCount;
    private long _liveConnectionsEnded;
    private long _recordingDropped;

    /// <summary>Messages dropped for the recorder since the last record it got; stamped onto the next one.</summary>
    private SeqRange _gap;
    private string? _lastError;
    private ClientWebSocket? _socket;
    private bool _deadBySilence;
    private string? _reconnectReason;
    private Task? _run;

    public ExchangeConnection(
        ExchangeAdapter adapter,
        MessageBufferPool pool,
        ChannelWriter<MessageBuffer>[] writers,
        EngineOptions options,
        ConnectionEventLog events,
        EngineMetrics? metrics = null,
        ILogger? logger = null,
        ChannelWriter<MessageBuffer>? recorder = null,
        HistoryFeed? history = null)
    {
        _history = history;
        _adapter = adapter;
        _pool = pool;
        _writers = writers;
        _recorder = recorder;
        _options = options;
        _events = events;
        _metrics = metrics;
        _logger = logger ?? NullLogger.Instance;
        _exchangeTag = new TagList { { "exchange", adapter.Name } };
    }

    public ExchangeAdapter Adapter => _adapter;

    public ConnectionState State => (ConnectionState)Volatile.Read(ref _state);

    public long ConnectionId => Interlocked.Read(ref _connectionId);

    /// <summary>Reconnects after failures. Scheduled closes by the exchange are not counted.</summary>
    public int ReconnectCount => Volatile.Read(ref _reconnectCount);

    /// <summary>Live connections that ended, for any reason but stopping, scheduled closes included; each is also published to the history feed.</summary>
    public long LiveConnectionsEnded => Interlocked.Read(ref _liveConnectionsEnded);

    public string? LastError => Volatile.Read(ref _lastError);

    /// <summary>Records dropped because the recorder's queue was full.</summary>
    public long RecordingDropped => Interlocked.Read(ref _recordingDropped);

    /// <summary><see cref="Stopwatch"/> timestamp of the last message, or 0 before the first one.</summary>
    public long LastMessageTimestamp => Interlocked.Read(ref _lastMessageTimestamp);

    /// <summary>
    /// Ends connection <paramref name="connectionId"/> and connects again with the normal delays, for
    /// example because its book needs a new snapshot. Does nothing if that connection is no longer live.
    /// Safe to call from any thread.
    /// </summary>
    public void RequestReconnect(long connectionId, string reason)
    {
        if (State != ConnectionState.Live || ConnectionId != connectionId)
        {
            return;
        }
        Volatile.Write(ref _reconnectReason, reason);
        Volatile.Read(ref _socket)?.Abort();
    }

    public void Start()
    {
        SetState(ConnectionState.Connecting);
        _run = Task.Run(RunAsync);
    }

    /// <summary>Closes the connection and completes the consumer queues once the last message is queued.</summary>
    public async Task StopAsync()
    {
        _stop.Cancel();
        Volatile.Read(ref _socket)?.Abort();
        if (_run is not null)
        {
            await _run;
        }
    }

    private async Task RunAsync()
    {
        var stopToken = _stop.Token;
        var attempt = 0;
        try
        {
            while (!stopToken.IsCancellationRequested)
            {
                var end = await ConnectAndReceiveAsync(stopToken);
                if (end.Kind == EndKind.Stopped || stopToken.IsCancellationRequested)
                {
                    break;
                }

                if (end.LiveFor > TimeSpan.Zero)
                {
                    Interlocked.Increment(ref _liveConnectionsEnded);
                    _history?.Add(new ReconnectRecord(_adapter.Id, _adapter.Name, DateTime.UtcNow,
                        end.Detail is null ? end.Event : $"{end.Event}: {end.Detail}"));
                }

                TimeSpan delay;
                switch (end.Kind)
                {
                    case EndKind.Blocked:
                        attempt = 0;
                        delay = _options.BlockedRetryDelay;
                        SetState(ConnectionState.Blocked);
                        AddFailure(end, $"retrying in {Format(delay)}");
                        break;
                    case EndKind.Scheduled:
                        attempt = 0;
                        delay = TimeSpan.Zero;
                        SetState(ConnectionState.Connecting);
                        AddEvent(end.Event, end.Detail);
                        break;
                    default:
                        if (end.LiveFor >= _options.BackoffResetAfter)
                        {
                            attempt = 0;
                        }
                        delay = _options.ReconnectDelays[Math.Min(attempt, _options.ReconnectDelays.Count - 1)];
                        attempt++;
                        Interlocked.Increment(ref _reconnectCount);
                        SetState(ConnectionState.Reconnecting);
                        AddFailure(end, $"reconnecting in {Format(delay)}");
                        break;
                }
                _metrics?.Reconnects.Add(1, new TagList { { "exchange", _adapter.Name }, { "reason", end.Reason } });

                try
                {
                    await Task.Delay(delay, stopToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            if (!_gap.IsEmpty)
            {
                _logger.LogWarning("{Exchange}: messages {First} to {Last} were not recorded ({Count} dropped at stop)",
                    _adapter.Name, _gap.First, _gap.Last, _gap.Count);
            }
            foreach (var writer in _writers)
            {
                writer.TryComplete();
            }
            _recorder?.TryComplete();
            SetState(ConnectionState.Stopped);
            AddEvent("Stopped");
        }
    }

    private async Task<ConnectionEnd> ConnectAndReceiveAsync(CancellationToken stopToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        Volatile.Write(ref _reconnectReason, null);
        Volatile.Write(ref _socket, socket);
        if (stopToken.IsCancellationRequested)
        {
            return ConnectionEnd.Stopped;
        }

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
            connectTimeout.CancelAfter(_options.ConnectTimeout);
            await socket.ConnectAsync(_adapter.Uri, connectTimeout.Token);
            foreach (var message in _adapter.SubscribeMessages)
            {
                await socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, connectTimeout.Token);
            }
        }
        catch (Exception) when (stopToken.IsCancellationRequested)
        {
            return ConnectionEnd.Stopped;
        }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.UnavailableForLegalReasons)
        {
            return new ConnectionEnd(EndKind.Blocked, "blocked", "Blocked (HTTP 451)", null, TimeSpan.Zero);
        }
        catch (OperationCanceledException)
        {
            return new ConnectionEnd(EndKind.Failed, "connect_failed", "Connect timed out", null, TimeSpan.Zero);
        }
        catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException)
        {
            var status = socket.HttpStatusCode == 0 ? null : $"HTTP {(int)socket.HttpStatusCode}: ";
            return new ConnectionEnd(EndKind.Failed, "connect_failed", "Connect failed", status + e.Message, TimeSpan.Zero);
        }

        var connectionId = Interlocked.Increment(ref _connectionId);
        var connectedAt = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _lastMessageTimestamp, connectedAt);
        Volatile.Write(ref _deadBySilence, false);
        SetState(ConnectionState.Live);
        AddEvent("Connected", $"connection {connectionId}");
        Record(RecordType.ConnectionOpened, connectionId, _adapter.Uri.AbsoluteUri);

        using var watchdogStop = new CancellationTokenSource();
        var watchdog = WatchdogAsync(socket, watchdogStop.Token);
        ConnectionEnd end;
        try
        {
            await ReceiveLoopAsync(socket, connectionId);
            var detail = socket.CloseStatus is { } status ? $"{(int)status} {socket.CloseStatusDescription}".TrimEnd() : null;
            end = new ConnectionEnd(EndKind.Failed, "closed", "Closed by server", detail, TimeSpan.Zero);
        }
        catch (Exception) when (stopToken.IsCancellationRequested)
        {
            end = ConnectionEnd.Stopped;
        }
        catch (Exception) when (Volatile.Read(ref _deadBySilence))
        {
            end = new ConnectionEnd(EndKind.Failed, "dead", $"Dead after {Format(_options.DeadAfter)} silence", null, TimeSpan.Zero);
        }
        catch (Exception) when (Volatile.Read(ref _reconnectReason) is { } reason)
        {
            end = new ConnectionEnd(EndKind.Failed, "requested", "Reconnect requested", reason, TimeSpan.Zero);
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException or InvalidDataException)
        {
            end = new ConnectionEnd(EndKind.Failed, "error", "Connection lost", e.Message, TimeSpan.Zero);
        }
        finally
        {
            watchdogStop.Cancel();
            await watchdog;
        }

        var liveFor = Stopwatch.GetElapsedTime(connectedAt);
        if (end.Kind == EndKind.Failed
            && end.Reason is not ("dead" or "requested")
            && _adapter.MaxConnectionLifetime is { } lifetime
            && liveFor >= lifetime - _options.ScheduledCloseTolerance)
        {
            end = new ConnectionEnd(EndKind.Scheduled, "scheduled", $"Closed by server ({Format(lifetime)} limit)", end.Detail, liveFor);
        }
        Record(RecordType.ConnectionClosed, connectionId, end.Detail is null ? end.Event : $"{end.Event}: {end.Detail}");
        return end with { LiveFor = liveFor };
    }

    /// <summary>Receives messages until the server closes the connection. Allocation-free per message.</summary>
    internal async Task ReceiveLoopAsync(WebSocket socket, long connectionId)
    {
        MessageBuffer? buffer = null;
        try
        {
            while (true)
            {
                buffer ??= _pool.Rent();
                var received = 0;
                ValueWebSocketReceiveResult result;
                do
                {
                    if (received == buffer.Bytes.Length)
                    {
                        if (received >= _options.MaxMessageBytes)
                        {
                            throw new InvalidDataException($"Message larger than {_options.MaxMessageBytes} bytes");
                        }
                        buffer.Grow(received);
                    }
                    result = await socket.ReceiveAsync(buffer.Bytes.AsMemory(received), CancellationToken.None);
                    received += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
                Interlocked.Exchange(ref _lastMessageTimestamp, Stopwatch.GetTimestamp());
                if (received == 0)
                {
                    continue;
                }

                buffer.Length = received;
                buffer.ExchangeId = _adapter.Id;
                buffer.ConnectionId = connectionId;
                buffer.Seq = ++_seq;
                buffer.ArrivalUnixNs = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;
                buffer.ReceivedTimestamp = Stopwatch.GetTimestamp();
                buffer.Counter = _recorder is null ? _writers.Length : _writers.Length + 1;
                _metrics?.MessagesReceived.Add(1, _exchangeTag);
                _metrics?.BytesReceived.Add(received, _exchangeTag);

                var message = buffer;
                buffer = null;
                if (_recorder is not null)
                {
                    QueueForRecorder(message);
                }
                foreach (var writer in _writers)
                {
                    if (!writer.TryWrite(message))
                    {
                        await writer.WriteAsync(message);
                    }
                }
            }
        }
        finally
        {
            if (buffer is not null)
            {
                buffer.Counter = 1;
                buffer.Release();
            }
        }
    }

    /// <summary>
    /// Hands a record to the recorder without ever waiting. If its queue is full, the record is released
    /// on the recorder's behalf and a dropped message joins the gap, which goes with the next record that fits.
    /// </summary>
    private void QueueForRecorder(MessageBuffer buffer)
    {
        var seq = buffer.Seq;
        var isMessage = buffer.Type == RecordType.Message;
        buffer.GapFirstSeq = _gap.First;
        buffer.GapLastSeq = _gap.Last;
        buffer.GapCount = _gap.Count;
        if (_recorder!.TryWrite(buffer))
        {
            _gap.Clear();
            return;
        }

        buffer.Release();
        Interlocked.Increment(ref _recordingDropped);
        _metrics?.RecordingDropped.Add(1, _exchangeTag);
        if (isMessage)
        {
            _gap.Add(seq);
        }
    }

    /// <summary>Sends a connection record to the recorder only; it carries the last message's seq.</summary>
    private void Record(RecordType type, long connectionId, string text)
    {
        if (_recorder is null)
        {
            return;
        }
        var buffer = _pool.Rent();
        // At most 3 UTF-8 bytes per UTF-16 char, so the text always fits.
        buffer.Length = Encoding.UTF8.GetBytes(text.AsSpan(0, Math.Min(text.Length, buffer.Bytes.Length / 3)), buffer.Bytes);
        buffer.Type = type;
        buffer.ExchangeId = _adapter.Id;
        buffer.ConnectionId = connectionId;
        buffer.Seq = _seq;
        buffer.ArrivalUnixNs = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;
        buffer.Counter = 1;
        QueueForRecorder(buffer);
    }

    private async Task WatchdogAsync(ClientWebSocket socket, CancellationToken token)
    {
        using var timer = new PeriodicTimer(_options.WatchdogInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastMessageTimestamp)) >= _options.DeadAfter)
                {
                    Volatile.Write(ref _deadBySilence, true);
                    socket.Abort();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetState(ConnectionState state) => Volatile.Write(ref _state, (int)state);

    private void AddFailure(ConnectionEnd end, string next)
    {
        var detail = end.Detail is null ? next : $"{end.Detail}; {next}";
        Volatile.Write(ref _lastError, end.Detail is null ? end.Event : $"{end.Event}: {end.Detail}");
        _events.Add(_adapter.Name, end.Event, detail);
        _logger.LogWarning("{Exchange}: {Event} ({Detail})", _adapter.Name, end.Event, detail);
    }

    private void AddEvent(string @event, string? detail = null)
    {
        _events.Add(_adapter.Name, @event, detail);
        if (detail is null)
        {
            _logger.LogInformation("{Exchange}: {Event}", _adapter.Name, @event);
        }
        else
        {
            _logger.LogInformation("{Exchange}: {Event} ({Detail})", _adapter.Name, @event, detail);
        }
    }

    private static string Format(TimeSpan time) => time switch
    {
        { TotalHours: >= 1 } when time.TotalHours % 1 == 0 => $"{time.TotalHours:0} h",
        { TotalMinutes: >= 1 } when time.TotalMinutes % 1 == 0 => $"{time.TotalMinutes:0} min",
        { TotalSeconds: >= 1 } => $"{time.TotalSeconds:0.#} s",
        _ => $"{time.TotalMilliseconds:0} ms",
    };

    private enum EndKind
    {
        Stopped,
        Failed,
        Blocked,
        Scheduled,
    }

    private readonly record struct ConnectionEnd(EndKind Kind, string Reason, string Event, string? Detail, TimeSpan LiveFor)
    {
        public static ConnectionEnd Stopped => new(EndKind.Stopped, "stopped", "Stopped", null, TimeSpan.Zero);
    }
}
