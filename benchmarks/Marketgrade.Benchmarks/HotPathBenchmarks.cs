using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Marketgrade.Engine;
using Marketgrade.Engine.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Benchmarks;

/// <summary>
/// The whole per-message path: WebSocket receive, stamping, queueing, waking the book worker's thread,
/// classifying and releasing. The worker has no pair rules here, so it keeps no book; the order book
/// benchmarks measure parsing, applying and checking.
/// With <see cref="Recording"/> the recorder is the second consumer and writes every message to disk
/// (or drops it for the recorder when it falls behind). Real messages are replayed as WebSocket frames
/// from memory, so only engine code is measured. Allocations are reported per message, on all threads;
/// the WebSocket created per run is spread over all of them.
/// </summary>
[MemoryDiagnoser]
public class HotPathBenchmarks
{
    private const int MessageCount = 100_000;

    private ReplayStream _stream = null!;
    private ExchangeConnection _connection = null!;
    private BookWorker _worker = null!;
    private Channel<MessageBuffer> _queue = null!;
    private ChannelWriter<MessageBuffer> _writer = null!;
    private EngineMetrics _metrics = null!;
    private HttpClient _http = null!;
    private Channel<MessageBuffer>? _recorderQueue;
    private RecordingManager? _recording;
    private Task? _recorderTask;
    private string _folder = "";
    private int _iteration;
    private long _expected;

    [Params("kraken", "coinbase", "binance")]
    public string Exchange { get; set; } = "";

    [Params(false, true)]
    public bool Recording { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        string[] files = Exchange switch
        {
            "kraken" => ["kraken/data-book-update.json", "kraken/heartbeat.json"],
            "coinbase" => ["coinbase/data-l2-update.json", "coinbase/heartbeat.json"],
            _ => ["binance/data-depth-update.json"],
        };
        var messages = files.Select(Fixtures.Read).ToArray();
        _stream = new ReplayStream(BuildFrames(messages));

        var adapter = Fixtures.AdapterFor($"{Exchange}/");
        // The replayed frames arrive far faster than any exchange sends, so the queue fills up. A pool that
        // covers the whole queue keeps pool misses (counted by the pool_misses metric) out of the measurement.
        var defaults = new EngineOptions();
        var options = defaults with { PoolSize = defaults.QueueCapacity + defaults.RecorderQueueCapacity + 16 };
        var pool = new MessageBufferPool(options.PoolSize, options.BufferSize);
        _metrics = new EngineMetrics();
        _queue = Channel.CreateBounded<MessageBuffer>(new BoundedChannelOptions(options.QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var signal = new ManualResetEventSlim();
        _writer = new SignalingWriter(_queue.Writer, signal);
        var events = new ConnectionEventLog(200);
        if (Recording)
        {
            _folder = Path.Combine(Path.GetTempPath(), "marketgrade-benchmarks", Guid.NewGuid().ToString("N"));
            _recording = new RecordingManager(RecordingSettings.Default(FolderFor(0)), options, TimeProvider.System, events, NullLogger.Instance, _metrics);
            _recorderQueue = Channel.CreateBounded<MessageBuffer>(new BoundedChannelOptions(options.RecorderQueueCapacity)
            {
                SingleReader = true,
            });
            var recorder = _recording.AddRecorder(adapter);
            _recorderTask = Task.Run(() => recorder.RunAsync(_recorderQueue.Reader));
        }
        _connection = new ExchangeConnection(adapter, pool, [_writer], options, events, _metrics, recorder: _recorderQueue?.Writer);
        _http = new HttpClient();
        _worker = new BookWorker(adapter, _connection, _queue.Reader, signal, pool, _http, new ClockOffset(), options, events,
            NullLogger.Instance, _metrics, _recorderQueue?.Writer);
        _worker.Start(rules: null);
    }

    /// <summary>Moves the recorder to a new folder and deletes the one before, so the benchmark does not fill the disk.</summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
        if (_recording is null)
        {
            return;
        }
        _iteration++;
        _recording.Apply(_recording.Settings with { Folder = FolderFor(_iteration) });
        if (_iteration >= 2 && Directory.Exists(FolderFor(_iteration - 2)))
        {
            Directory.Delete(FolderFor(_iteration - 2), recursive: true);
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _writer.Complete();
        _worker.Completion.Wait();
        _http.Dispose();
        _recorderQueue?.Writer.Complete();
        _recorderTask?.Wait();
        if (Recording && Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
        _metrics.Dispose();
    }

    private string FolderFor(int iteration) => Path.Combine(_folder, iteration.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Benchmark(OperationsPerInvoke = MessageCount)]
    public async Task ReceiveQueueClassifyRelease()
    {
        _stream.Rewind();
        using var socket = WebSocket.CreateFromStream(_stream, new WebSocketCreationOptions());
        await _connection.ReceiveLoopAsync(socket, connectionId: 1);

        _expected += MessageCount;
        var spin = new SpinWait();
        while (Total() < _expected)
        {
            spin.SpinOnce();
        }
    }

    private long Total() =>
        _worker.DataMessages + _worker.HeartbeatMessages + _worker.ControlMessages + _worker.ErrorMessages;

    /// <summary>Server-to-client text frames (unmasked), cycling through the messages, then a close frame.</summary>
    private static byte[] BuildFrames(byte[][] messages)
    {
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[10];
        for (var i = 0; i < MessageCount; i++)
        {
            var payload = messages[i % messages.Length];
            header[0] = 0x81;
            int headerLength;
            if (payload.Length < 126)
            {
                header[1] = (byte)payload.Length;
                headerLength = 2;
            }
            else if (payload.Length <= ushort.MaxValue)
            {
                header[1] = 126;
                BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)payload.Length);
                headerLength = 4;
            }
            else
            {
                header[1] = 127;
                BinaryPrimitives.WriteUInt64BigEndian(header[2..], (ulong)payload.Length);
                headerLength = 10;
            }
            output.Write(header[..headerLength]);
            output.Write(payload);
        }
        output.Write([0x88, 0x02, 0x03, 0xE8]);
        return output.ToArray();
    }

    /// <summary>Plays back the same bytes on every run and discards everything written.</summary>
    private sealed class ReplayStream(byte[] data) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanWrite => true;

        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Rewind() => _position = 0;

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(buffer.Length, data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Read(buffer.Span));

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
