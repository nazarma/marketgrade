using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Recording;

/// <summary>What all recorders share: the current settings, the low-disk pause and the compression queue.</summary>
internal sealed class RecordingManager
{
    private RecordingSettings _settings;
    private int _paused;

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

    /// <summary>Closed files of finished hours, waiting for compression.</summary>
    public Channel<string> CompressionQueue { get; } = Channel.CreateUnbounded<string>();

    public void Apply(RecordingSettings settings) => Volatile.Write(ref _settings, settings);

    public void QueueCompression(string path) => CompressionQueue.Writer.TryWrite(path);
}
