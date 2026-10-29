using Marketgrade.Engine.Books;
using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine;

/// <summary>Immutable snapshot of all exchanges, rebuilt once per status interval.</summary>
/// <param name="Recording">Null when the engine runs without recording.</param>
/// <param name="ClockOffsetMs">How far the local clock is ahead of NTP time, or null if unknown (delays are then uncorrected).</param>
public sealed record EngineStatus(
    DateTime TimeUtc,
    IReadOnlyList<ExchangeStatus> Exchanges,
    RecordingStatus? Recording = null,
    double? ClockOffsetMs = null);

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

/// <param name="MessagesPerSecond">Book data messages only; trade, heartbeat, control and error messages are counted separately.</param>
/// <param name="BytesPerSecond">All message bytes.</param>
/// <param name="MsSinceLastMessage">Time since the last message or connect, or null before the first connect.</param>
/// <param name="ReconnectCount">Reconnects after failures; scheduled closes by the exchange are not counted.</param>
/// <param name="BestBid">Best bid with the pair's decimals, or null while the book is not valid. Likewise <paramref name="BestAsk"/> and <paramref name="Spread"/>.</param>
/// <param name="MaxBidLevels">The most levels the bid side ever held, for tuning the book capacity. Likewise <paramref name="MaxAskLevels"/>.</param>
/// <param name="DelayP50Ms">Median delay from the exchange's timestamp to arrival over the last minute, clock-corrected when the offset is known; null without data.</param>
/// <param name="ProcessingP50Ns">Median time to parse, apply and check one message over the last minute; null without data.</param>
/// <param name="CatchUpMessages">Messages since the last snapshot that arrived within 100 ms after it, left out of the delay statistics.</param>
/// <param name="CatchUpMaxDelayMs">The largest delay of those messages, or null if there were none.</param>
/// <param name="ExchangeHoldP50Ms">Median time the exchange held changes before sending them (message timestamp minus its oldest change time), over the last minute; only Coinbase reports change times, null otherwise.</param>
/// <param name="QueueWaitP50Ns">Median time from receiving a message to the book worker starting on it, over the last minute; null without data.</param>
/// <param name="GapsToday">Times the book became invalid today (UTC).</param>
/// <param name="Trades">Trades parsed (a trade message can hold several), without those sent as history after subscribing.</param>
/// <param name="TradeFailures">Trade messages that could not be read; they never affect the book.</param>
/// <param name="DelayCount">Delays measured over the last minute, and of those <paramref name="DelayNegative"/> below zero.</param>
/// <param name="DelayLowerBound">So many delays were negative (counted as zero) that the delay figures are a lower bound, see <see cref="NegativeDelays"/>.</param>
/// <param name="DelayAheadMs">How far below zero the negative delays went, 99th percentile; null without any.</param>
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
    long Bytes,
    BookState BookState = BookState.NoPairRules,
    string? BestBid = null,
    string? BestAsk = null,
    string? Spread = null,
    int BidLevels = 0,
    int AskLevels = 0,
    int MaxBidLevels = 0,
    int MaxAskLevels = 0,
    double? DelayP50Ms = null,
    double? DelayP99Ms = null,
    long? ProcessingP50Ns = null,
    long? ProcessingP99Ns = null,
    int GapsToday = 0,
    BookGap? LastGap = null,
    long? QueueWaitP50Ns = null,
    long? QueueWaitP99Ns = null,
    long CatchUpMessages = 0,
    double? CatchUpMaxDelayMs = null,
    double? ExchangeHoldP50Ms = null,
    double? ExchangeHoldP99Ms = null,
    long TradeMessages = 0,
    long Trades = 0,
    double TradesPerSecond = 0,
    long TradeFailures = 0,
    long DelayCount = 0,
    long DelayNegative = 0,
    bool DelayLowerBound = false,
    double? DelayAheadMs = null);
