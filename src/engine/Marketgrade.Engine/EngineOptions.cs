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

    /// <summary>After a write error, a recorder tries again after this delay.</summary>
    public TimeSpan RecordingRetryDelay { get; init; } = TimeSpan.FromSeconds(5);
}
