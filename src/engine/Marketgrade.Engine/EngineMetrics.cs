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
    }

    public Counter<long> MessagesReceived { get; }

    public Counter<long> BytesReceived { get; }

    public Counter<long> Reconnects { get; }

    public Counter<long> PoolMisses { get; }

    public void Dispose() => _meter.Dispose();
}
