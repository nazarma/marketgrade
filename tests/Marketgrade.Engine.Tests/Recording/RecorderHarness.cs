using System.Text;
using System.Threading.Channels;
using Marketgrade.Engine.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Marketgrade.Engine.Tests.Recording;

/// <summary>
/// One recorder with a fake clock, writing into a temporary folder. Tests either drive it directly
/// (<see cref="Write"/>, deterministic) or run its loop with <see cref="Start"/> and <see cref="Send"/>.
/// </summary>
internal sealed class RecorderHarness
{
    private readonly Channel<MessageBuffer> _queue = Channel.CreateUnbounded<MessageBuffer>();
    private Task? _run;

    public RecorderHarness(Func<RecordingSettings, RecordingSettings>? configure = null, DateTimeOffset? now = null, EngineOptions? options = null)
    {
        Folder = TestRecordings.CreateFolder();
        Time = new FakeTimeProvider(now ?? new DateTimeOffset(2026, 3, 29, 10, 30, 0, TimeSpan.Zero));
        var settings = RecordingSettings.Default(Path.Combine(Folder, "recordings"));
        Manager = new RecordingManager(configure?.Invoke(settings) ?? settings, options ?? new EngineOptions(), Time, Events, NullLogger.Instance);
        Recorder = Manager.AddRecorder(Adapter);
    }

    public string Folder { get; }

    public FakeTimeProvider Time { get; }

    public ConnectionEventLog Events { get; } = new(100);

    public TestAdapter Adapter { get; } = new(new Uri("ws://127.0.0.1/ws"));

    public MessageBufferPool Pool { get; } = new(16, 1024);

    public RecordingManager Manager { get; }

    public Recorder Recorder { get; }

    public string RecordingsFolder => Manager.Settings.Folder;

    public static long UnixNs(DateTimeOffset time) => (time.UtcTicks - DateTime.UnixEpoch.Ticks) * 100;

    public string PathFor(DateTimeOffset hour, int part = 1, bool compressed = false) =>
        RecordingFiles.PathFor(RecordingsFolder, Adapter.Name, RecordingFiles.HourOf(hour), part, compressed);

    public MessageBuffer Buffer(long seq, string payload, DateTimeOffset? arrival = null, RecordType type = RecordType.Message, long connectionId = 1)
    {
        var buffer = Pool.Rent();
        var bytes = Encoding.UTF8.GetBytes(payload);
        while (bytes.Length > buffer.Bytes.Length)
        {
            buffer.Grow(0);
        }
        bytes.CopyTo(buffer.Bytes, 0);
        buffer.Length = bytes.Length;
        buffer.Type = type;
        buffer.Seq = seq;
        buffer.ConnectionId = connectionId;
        buffer.ExchangeId = Adapter.Id;
        buffer.ArrivalUnixNs = UnixNs(arrival ?? Time.GetUtcNow());
        buffer.Counter = 1;
        return buffer;
    }

    /// <summary>Processes buffers as one batch, without the recorder's loop.</summary>
    public void Write(params MessageBuffer[] buffers)
    {
        Recorder.BeginBatch();
        foreach (var buffer in buffers)
        {
            Recorder.Process(buffer);
        }
    }

    public void Close() => Recorder.CloseFile();

    public void Start() => _run = Task.Run(() => Recorder.RunAsync(_queue.Reader));

    public void Send(MessageBuffer buffer) => Assert.True(_queue.Writer.TryWrite(buffer));

    public async Task StopAsync()
    {
        _queue.Writer.Complete();
        await _run!;
    }

    public List<string> QueuedForCompression()
    {
        var paths = new List<string>();
        while (Manager.CompressionQueue.Reader.TryRead(out var path))
        {
            paths.Add(path);
        }
        return paths;
    }

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
