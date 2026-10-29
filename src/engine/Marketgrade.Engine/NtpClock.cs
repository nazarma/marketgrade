using System.Diagnostics;

namespace Marketgrade.Engine;

/// <summary>What <see cref="NtpClock.Read"/> knows about a moment.</summary>
internal enum ClockReading
{
    /// <summary>Not synced yet: delays use the wall clock.</summary>
    Unknown,

    /// <summary>The clocks parted (a step or a wake from sleep) and a sync is running: delays are skipped.</summary>
    Held,

    Known,
}

/// <summary>
/// NTP time from the monotonic clock, shared by all book workers. A sync gives the NTP time at a
/// <see cref="Stopwatch"/> timestamp; a later moment is that time plus the monotonic time since, corrected
/// by the monotonic clock's measured rate error, so a step of the wall clock (macOS setting the time)
/// changes no delay. Unknown until the first sync.
/// </summary>
internal sealed class NtpClock
{
    private static readonly double s_nsPerTick = 1e9 / Stopwatch.Frequency;

    private Anchor? _anchor;

    /// <param name="ntpUnixNs">NTP time at <paramref name="timestamp"/>, Unix nanoseconds.</param>
    /// <param name="rate">How much faster NTP time runs than the monotonic clock, as a fraction (1e-6 is 1 ppm).</param>
    public void Sync(long ntpUnixNs, long timestamp, double rate = 0) =>
        Volatile.Write(ref _anchor, new Anchor(ntpUnixNs, timestamp, s_nsPerTick * (1 + rate), Held: false));

    /// <summary>Delays are skipped until the next <see cref="Sync"/>; nothing changes before the first sync.</summary>
    public void Hold()
    {
        if (Volatile.Read(ref _anchor) is { } anchor)
        {
            Volatile.Write(ref _anchor, anchor with { Held = true });
        }
    }

    /// <summary>Forgets the sync: delays use the wall clock again, as before the first sync.</summary>
    public void Clear() => Volatile.Write(ref _anchor, null);

    /// <summary>NTP time at a <see cref="Stopwatch"/> timestamp, Unix nanoseconds, when it is <see cref="ClockReading.Known"/>.</summary>
    public ClockReading Read(long timestamp, out long unixNs)
    {
        var anchor = Volatile.Read(ref _anchor);
        if (anchor is null || anchor.Held)
        {
            unixNs = 0;
            return anchor is null ? ClockReading.Unknown : ClockReading.Held;
        }
        unixNs = anchor.NtpUnixNs + (long)((timestamp - anchor.Timestamp) * anchor.NsPerTick);
        return ClockReading.Known;
    }

    /// <summary>The wall clock minus NTP time at a moment, in nanoseconds; null unless it is known.</summary>
    public long? OffsetNs(long wallUnixNs, long timestamp) =>
        Read(timestamp, out var ntp) == ClockReading.Known ? wallUnixNs - ntp : null;

    private sealed record Anchor(long NtpUnixNs, long Timestamp, double NsPerTick, bool Held);
}
