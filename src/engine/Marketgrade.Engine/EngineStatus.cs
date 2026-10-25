namespace Marketgrade.Engine;

/// <summary>Immutable snapshot of all exchanges, rebuilt once per status interval.</summary>
public sealed record EngineStatus(DateTime TimeUtc, IReadOnlyList<ExchangeStatus> Exchanges);

/// <param name="MessagesPerSecond">Data messages only; heartbeats, control and error messages are counted separately.</param>
/// <param name="BytesPerSecond">All message bytes.</param>
/// <param name="MsSinceLastMessage">Time since the last message or connect, or null before the first connect.</param>
/// <param name="ReconnectCount">Reconnects after failures; scheduled closes by the exchange are not counted.</param>
public sealed record ExchangeStatus(
    int Id,
    string Name,
    string Pair,
    ConnectionState State,
    double MessagesPerSecond,
    double BytesPerSecond,
    long? MsSinceLastMessage,
    long ConnectionId,
    int ReconnectCount,
    string? LastError,
    long DataMessages,
    long HeartbeatMessages,
    long ControlMessages,
    long ErrorMessages,
    long Bytes);
