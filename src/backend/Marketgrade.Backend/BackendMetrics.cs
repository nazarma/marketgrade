using System.Diagnostics.Metrics;

namespace Marketgrade.Backend;

/// <summary>The <c>Marketgrade.Backend</c> meter: <c>hub_messages_sent</c>, tagged by message type (status, event, flow).</summary>
public sealed class BackendMetrics : IDisposable
{
    private readonly Meter _meter = new("Marketgrade.Backend");

    public BackendMetrics() => HubMessagesSent = _meter.CreateCounter<long>("hub_messages_sent");

    public Counter<long> HubMessagesSent { get; }

    public void Dispose() => _meter.Dispose();
}
