using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Recording;

/// <summary>Size of the recordings in the current folder and free space on its drive, from the latest disk check.</summary>
/// <param name="TodayBytes">Files of the current UTC day.</param>
/// <param name="FreeBytes">Null if the drive could not be checked.</param>
internal sealed record DiskUsage(long TotalBytes, long TodayBytes, long? FreeBytes);

/// <summary>What all recorders share: the current settings, the low-disk pause and the compression queue.</summary>
internal sealed class RecordingManager
{
    private readonly List<Recorder> _recorders = [];
    private RecordingSettings _settings;
    private int _paused;
    private DiskUsage _diskUsage = new(0, 0, null);

    public RecordingManager(
        RecordingSettings settings,
        EngineOptions options,
        TimeProvider time,
        ConnectionEventLog events,
        ILogger logger,
        EngineMetrics? metrics = null)
    {
        _settings = settings;
        Options = options;
        Time = time;
        Events = events;
        Logger = logger;
        Metrics = metrics;
    }

    /// <summary>Recorders pick up a new reference between batches.</summary>
    public RecordingSettings Settings => Volatile.Read(ref _settings);

    public EngineOptions Options { get; }

    public TimeProvider Time { get; }

    public ConnectionEventLog Events { get; }

    public ILogger Logger { get; }

    public EngineMetrics? Metrics { get; }

    public bool Paused
    {
        get => Volatile.Read(ref _paused) == 1;
        set => Volatile.Write(ref _paused, value ? 1 : 0);
    }

    public DiskUsage DiskUsage
    {
        get => Volatile.Read(ref _diskUsage);
        set => Volatile.Write(ref _diskUsage, value);
    }

    /// <summary>Closed files of finished hours, waiting for compression.</summary>
    public Channel<string> CompressionQueue { get; } = Channel.CreateUnbounded<string>();

    public IReadOnlyList<Recorder> Recorders => _recorders;

    /// <summary>Creates the recorder of one exchange. All recorders are created before the engine starts.</summary>
    public Recorder AddRecorder(ExchangeAdapter adapter)
    {
        var recorder = new Recorder(adapter, this);
        _recorders.Add(recorder);
        return recorder;
    }

    /// <summary>True for a file a recorder is writing; it must not be compressed or deleted.</summary>
    public bool IsActive(string path) => _recorders.Any(r => r.ActiveFile == path);

    /// <summary>The exchange name as the metrics tag it, from the lower-case folder name.</summary>
    public string ExchangeName(string folderName) =>
        _recorders.FirstOrDefault(r => r.Adapter.Name.Equals(folderName, StringComparison.OrdinalIgnoreCase))?.Adapter.Name ?? folderName;

    public void Apply(RecordingSettings settings) => Volatile.Write(ref _settings, settings);

    public void QueueCompression(string path) => CompressionQueue.Writer.TryWrite(path);
}
