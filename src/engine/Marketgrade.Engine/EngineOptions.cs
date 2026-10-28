namespace Marketgrade.Engine;

/// <summary>All sizes, timeouts and delays of the engine. Tests shorten the timings to milliseconds.</summary>
public sealed record EngineOptions
{
    public TimeSpan WatchdogInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>A connection with no message of any kind for this long is dead.</summary>
    public TimeSpan DeadAfter { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Delays before reconnect attempts; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
        [.. new[] { 1, 2, 4, 8, 16, 30 }.Select(s => TimeSpan.FromSeconds(s))];

    /// <summary>A connection live for this long resets the reconnect delays to the first one.</summary>
    public TimeSpan BackoffResetAfter { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Retry interval after the exchange refused the connection for legal reasons (HTTP 451).</summary>
    public TimeSpan BlockedRetryDelay { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How early before the exchange's maximum lifetime a server close still counts as scheduled.</summary>
    public TimeSpan ScheduledCloseTolerance { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(1);

    public int QueueCapacity { get; init; } = 8_192;

    /// <summary>
    /// Smaller than <see cref="QueueCapacity"/>: every queued message holds a pool buffer, so a stalled disk
    /// costs at most about 32 MB per exchange before messages are dropped for the recorder.
    /// </summary>
    public int RecorderQueueCapacity { get; init; } = 2_048;

    public int PoolSize { get; init; } = 1_024;

    public int BufferSize { get; init; } = 16 * 1024;

    /// <summary>Safety limit; a larger message is treated as a broken connection.</summary>
    public int MaxMessageBytes { get; init; } = 64 * 1024 * 1024;

    public int EventLogSize { get; init; } = 200;

    /// <summary>How often the recording worker compresses finished hours and checks the disk.</summary>
    public TimeSpan RecordingCheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often old recordings are deleted and missed hours are compressed.</summary>
    public TimeSpan RetentionInterval { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>An uncompressed file left behind (recording turned off, app restarted) is compressed this long after its hour ends.</summary>
    public TimeSpan FinishedHourGrace { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Recording pauses when the recordings drive has less free space than this...</summary>
    public long LowDiskPauseBytes { get; init; } = 1L * 1024 * 1024 * 1024;

    /// <summary>...and resumes when it has more than this.</summary>
    public long LowDiskResumeBytes { get; init; } = 2L * 1024 * 1024 * 1024;

    /// <summary>After a write error, a recorder tries again after this delay.</summary>
    public TimeSpan RecordingRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Levels per side; Kraken keeps 1,000, and an update can add a few before the cut.</summary>
    public int KrakenBookCapacity { get; init; } = 2_000;

    /// <summary>Starting levels per side; a full side doubles. The Coinbase snapshot has about 20,000 per side.</summary>
    public int CoinbaseBookCapacity { get; init; } = 50_000;

    /// <summary>Starting levels per side; a full side doubles. The REST snapshot has at most 5,000 per side.</summary>
    public int BinanceBookCapacity { get; init; } = 20_000;

    /// <summary>Binance depth snapshots are fetched at most this often: one costs request weight 250.</summary>
    public TimeSpan BinanceResyncInterval { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan BinanceSnapshotTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A longer pause between the book worker's housekeeping passes (the computer slept) counts as time the app was not running.</summary>
    public TimeSpan AsleepAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often the book worker checks its connection, rotates statistics and publishes them while no message arrives.</summary>
    public TimeSpan BookHousekeepingInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}
