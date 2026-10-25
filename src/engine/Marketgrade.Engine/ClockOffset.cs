namespace Marketgrade.Engine;

/// <summary>How far the local clock is ahead of real time (NTP), shared by all book workers. Unknown until measured.</summary>
internal sealed class ClockOffset
{
    public const long Unknown = long.MinValue;

    private long _ns = Unknown;

    /// <summary>Local clock minus NTP time in nanoseconds, or <see cref="Unknown"/>.</summary>
    public long Ns => Interlocked.Read(ref _ns);

    public void Set(TimeSpan? offset) => Interlocked.Exchange(ref _ns, offset is { } value ? value.Ticks * 100 : Unknown);
}
