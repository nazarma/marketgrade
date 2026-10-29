namespace Marketgrade.Engine.Books;

/// <summary>
/// A delay below zero is an exchange timestamp later than the arrival time by internet time: the exchange's
/// clock is ahead, or this computer's measured offset from NTP is off by more than the delay. Such delays
/// count as zero, so when many are negative the percentiles are too low. Above <see cref="LowerBoundShare"/>
/// of the delays they are shown as a lower bound ("≤").
/// </summary>
public static class NegativeDelays
{
    public const double LowerBoundShare = 0.10;

    public static bool IsLowerBound(long negative, long delays) => delays > 0 && negative > delays * LowerBoundShare;
}
