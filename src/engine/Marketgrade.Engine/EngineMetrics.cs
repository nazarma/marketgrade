using System.Diagnostics.Metrics;

namespace Marketgrade.Engine;

/// <summary>The engine's metrics, visible with <c>dotnet-counters monitor --counters Marketgrade.Engine</c>.</summary>
public sealed class EngineMetrics : IDisposable
{
    public const string MeterName = "Marketgrade.Engine";

    private readonly Meter _meter = new(MeterName);

    public EngineMetrics()
    {
        MessagesReceived = _meter.CreateCounter<long>("messages_received", description: "Messages received from exchanges");
        BytesReceived = _meter.CreateCounter<long>("bytes_received", "By", "Message bytes received from exchanges");
        Reconnects = _meter.CreateCounter<long>("reconnects", description: "Connections ended, tagged with the reason");
        PoolMisses = _meter.CreateCounter<long>("pool_misses", description: "Buffers created because the pool was empty");
        RecordingBytesWritten = _meter.CreateCounter<long>("recording_bytes_written", "By", "Bytes written to recording files");
    }

    public Counter<long> MessagesReceived { get; }

    public Counter<long> BytesReceived { get; }

    public Counter<long> Reconnects { get; }

    public Counter<long> PoolMisses { get; }

    public Counter<long> RecordingBytesWritten { get; }

    public void ObserveEngine(Func<IEnumerable<Measurement<long>>> queueLengths, Func<IEnumerable<Measurement<int>>> connectionStates)
    {
        _meter.CreateObservableGauge("queue_length", queueLengths, description: "Messages waiting in a consumer queue");
        _meter.CreateObservableGauge("connection_state", connectionStates,
            description: "0 Connecting, 1 Live, 2 Reconnecting, 3 Blocked, 4 Stopped");
    }

    public void Dispose() => _meter.Dispose();
}
