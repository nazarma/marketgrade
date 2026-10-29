namespace Marketgrade.Backend;

/// <summary>
/// How much faster NTP time runs than the monotonic clock (a fraction; 1e-6 is 1 ppm), from the successful
/// syncs since the last reset: NTP time over monotonic time from the oldest kept sync to the newest, once
/// they are at least <see cref="MinSpan"/> apart, clamped to ±200 ppm. It keeps the last 6 syncs, about an
/// hour, so it follows a slow change of the rate. 0 until it is known.
/// </summary>
public sealed class ClockRate(long timestampFrequency)
{
    public const double Max = 200e-6;
    public static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(5);

    private const int Kept = 6;

    private readonly Queue<(long NtpUnixNs, long Timestamp)> _syncs = new();

    public double Rate { get; private set; }

    public void Add(long ntpUnixNs, long timestamp)
    {
        _syncs.Enqueue((ntpUnixNs, timestamp));
        if (_syncs.Count > Kept)
        {
            _syncs.Dequeue();
        }
        var (firstNtp, firstTimestamp) = _syncs.Peek();
        var monotonicNs = (timestamp - firstTimestamp) * 1e9 / timestampFrequency;
        if (monotonicNs >= MinSpan.TotalNanoseconds)
        {
            Rate = Math.Clamp((ntpUnixNs - firstNtp) / monotonicNs - 1, -Max, Max);
        }
    }

    public void Reset()
    {
        _syncs.Clear();
        Rate = 0;
    }
}
