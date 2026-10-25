using System.Diagnostics.Metrics;

namespace Marketgrade.Engine;

/// <summary>The engine's metrics, visible with <c>dotnet-counters monitor --counters Marketgrade.Engine</c>.</summary>
public sealed class EngineMetrics : IDisposable
{
    public const string MeterName = "Marketgrade.Engine";

    private readonly Meter _meter = new(MeterName);

    public EngineMetrics()
    {
        PoolMisses = _meter.CreateCounter<long>("pool_misses", description: "Buffers created because the pool was empty");
    }

    public Counter<long> PoolMisses { get; }

    public void Dispose() => _meter.Dispose();
}
