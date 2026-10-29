namespace Marketgrade.Engine.History;

/// <summary>
/// One exchange's statistics for one UTC minute, published once the minute is over (or when the engine
/// stops, for the minute it stopped in). A minute without one is a minute the app was not running.
/// </summary>
/// <param name="SecondsRunning">How much of the minute the engine was running and tracking this exchange.</param>
/// <param name="SecondsLive">Of that, how long the connection was live; likewise the book valid or invalid. Waiting for a snapshot is neither.</param>
/// <param name="DelayHistogram">All delays of the minute in <see cref="Books.CoarseHistogram"/> buckets.</param>
/// <param name="CatchUpMessages">Messages right after a snapshot, left out of the delays.</param>
/// <param name="Reconnects">Live connections that ended, scheduled closes included.</param>
/// <param name="BestBid">At the end of the minute, null while the book was not valid. Likewise <paramref name="BestAsk"/> and <paramref name="Spread"/>.</param>
/// <param name="DelayNegative">Delays below zero, counted as zero in the others (see <see cref="Books.NegativeDelays"/>).</param>
/// <param name="DelayAheadMs">How far below zero they went, 99th percentile; null without any.</param>
public sealed record MinuteStats(
    int ExchangeId,
    string Exchange,
    DateTime MinuteUtc,
    double SecondsRunning,
    double SecondsLive,
    double SecondsValid,
    double SecondsInvalid,
    long BookUpdates,
    long Trades,
    long Bytes,
    long DelayCount,
    double? DelayP50Ms,
    double? DelayP99Ms,
    double? DelayMaxMs,
    long[] DelayHistogram,
    long? ProcessingP50Ns,
    long? ProcessingP99Ns,
    double? HoldP50Ms,
    double? HoldP99Ms,
    long CatchUpMessages,
    int Reconnects,
    int GapsStarted,
    decimal? BestBid,
    decimal? BestAsk,
    decimal? Spread,
    long DelayNegative = 0,
    double? DelayAheadMs = null);

/// <summary>How a gap was found.</summary>
public enum GapKind
{
    /// <summary>Kraken's checksum of the top levels did not match.</summary>
    Checksum,

    /// <summary>A sequence number (Coinbase) or update ID (Binance) was skipped.</summary>
    Sequence,

    /// <summary>A message could not be read.</summary>
    Parse,

    /// <summary>The connection was lost or replaced, so the book missed whatever happened in between.</summary>
    ConnectionLost,
}

/// <summary>A completed gap, published once when the book is valid again (or when the engine stops during one).</summary>
/// <param name="RepairAction">What the app did about it, in words.</param>
public sealed record GapRecord(
    int ExchangeId,
    string Exchange,
    DateTime StartUtc,
    DateTime EndUtc,
    GapKind Kind,
    string Cause,
    string RepairAction)
{
    public double DurationSeconds => (EndUtc - StartUtc).TotalSeconds;
}

/// <summary>A live connection that ended, after which the engine connected again.</summary>
/// <param name="Reason">For example "Closed by server: 1006" or "Reconnect requested: Checksum mismatch ...".</param>
public sealed record ReconnectRecord(int ExchangeId, string Exchange, DateTime TimeUtc, string Reason);
