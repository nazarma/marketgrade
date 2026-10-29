using System.Runtime.InteropServices;

namespace Marketgrade.Backend;

/// <summary>
/// A monotonic clock that goes on while the computer sleeps, in nanoseconds, or null where there is none.
/// <see cref="System.Diagnostics.Stopwatch"/> stops during sleep on macOS (CLOCK_UPTIME_RAW) and Linux
/// (CLOCK_MONOTONIC), so how far this clock gets ahead of it is the time asleep.
/// </summary>
public static class SleepClock
{
    // macOS: CLOCK_MONOTONIC_RAW, the same clock as CLOCK_UPTIME_RAW but counting sleep. Linux: CLOCK_BOOTTIME.
    private const int MacMonotonicRaw = 4;
    private const int LinuxBootTime = 7;

    public static long? Ns()
    {
        if (OperatingSystem.IsMacOS())
        {
            return (long)clock_gettime_nsec_np(MacMonotonicRaw);
        }
        if (OperatingSystem.IsLinux())
        {
            return clock_gettime(LinuxBootTime, out var time) == 0 ? time.Seconds * 1_000_000_000 + time.Nanoseconds : null;
        }
        // GetTickCount64 counts sleep, to the millisecond.
        return OperatingSystem.IsWindows() ? Environment.TickCount64 * 1_000_000 : null;
    }

    [DllImport("libc")]
    private static extern ulong clock_gettime_nsec_np(int clock);

    [DllImport("libc")]
    private static extern int clock_gettime(int clock, out Timespec time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }
}
