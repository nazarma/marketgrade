namespace Marketgrade.Engine;

/// <summary>Limits a repeated warning to at most one per minute.</summary>
internal static class LogThrottle
{
    public const long Never = long.MinValue;

    private const long IntervalMs = 60_000;

    public static bool ShouldLog(ref long lastLoggedMs)
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastLoggedMs);
        return (last == Never || now - last >= IntervalMs)
            && Interlocked.CompareExchange(ref lastLoggedMs, now, last) == last;
    }
}
