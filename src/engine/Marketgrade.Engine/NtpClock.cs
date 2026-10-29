using System.Diagnostics;

namespace Marketgrade.Engine;

/// <summary>
/// NTP time from the monotonic clock, shared by all book workers. A sync gives the NTP time at a
/// <see cref="Stopwatch"/> timestamp; a later moment is that time plus the monotonic time since, so a step
/// of the wall clock (macOS setting the time) changes no delay. Unknown until the first sync.
/// </summary>
internal sealed class NtpClock
{
    private static readonly double s_nsPerTick = 1e9 / Stopwatch.Frequency;

    private Anchor? _anchor;

    /// <param name="ntpUnixNs">NTP time at <paramref name="timestamp"/>, Unix nanoseconds.</param>
    public void Sync(long ntpUnixNs, long timestamp) => Volatile.Write(ref _anchor, new Anchor(ntpUnixNs, timestamp));

    /// <summary>NTP time at a <see cref="Stopwatch"/> timestamp, Unix nanoseconds; false before the first sync.</summary>
    public bool TryUnixNs(long timestamp, out long unixNs)
    {
        var anchor = Volatile.Read(ref _anchor);
        unixNs = anchor is null ? 0 : anchor.NtpUnixNs + (long)((timestamp - anchor.Timestamp) * s_nsPerTick);
        return anchor is not null;
    }

    /// <summary>The wall clock minus NTP time at a moment, in nanoseconds; null before the first sync.</summary>
    public long? OffsetNs(long wallUnixNs, long timestamp) => TryUnixNs(timestamp, out var ntp) ? wallUnixNs - ntp : null;

    private sealed record Anchor(long NtpUnixNs, long Timestamp);
}
