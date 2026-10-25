using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine;

/// <summary>Immutable snapshot of all exchanges, rebuilt once per status interval.</summary>
/// <param name="Recording">Null when the engine runs without recording.</param>
public sealed record EngineStatus(DateTime TimeUtc, IReadOnlyList<ExchangeStatus> Exchanges, RecordingStatus? Recording = null);

/// <param name="TodayBytes">Size on disk of the current UTC day's recordings, all exchanges.</param>
/// <param name="TotalBytes">Size on disk of all recordings in the current folder.</param>
/// <param name="FreeBytes">Free space on the recordings drive, or null if unknown.</param>
/// <param name="DroppedMessages">Records dropped because a recorder could not keep up.</param>
public sealed record RecordingStatus(
    RecordingState State,
    string Folder,
    long TodayBytes,
    long TotalBytes,
    long? FreeBytes,
    long DroppedMessages,
    string? LastError);

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
