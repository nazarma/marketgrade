namespace Marketgrade.Engine.Books;

/// <summary>
/// Delays in 48 buckets of equal ratio (about 1.257) from 1 ms to 60 s, small enough to keep one per exchange
/// and minute and to add up over days. Values below 1 ms count in the first bucket, from 60 s on in the last.
/// A percentile is the geometric middle of its bucket, so it is approximate: within about 12 %.
/// </summary>
public static class CoarseHistogram
{
    public const int Buckets = 48;

    public const long MinNs = 1_000_000;

    public const long MaxNs = 60_000_000_000;

    /// <summary>Lower edges in nanoseconds; <c>s_edges[Buckets]</c> is <see cref="MaxNs"/>.</summary>
    private static readonly long[] s_edges =
        [.. Enumerable.Range(0, Buckets + 1).Select(i => (long)Math.Round(MinNs * Math.Pow((double)MaxNs / MinNs, (double)i / Buckets)))];

    public static int IndexOf(long ns)
    {
        if (ns < s_edges[1])
        {
            return 0;
        }
        if (ns >= s_edges[Buckets - 1])
        {
            return Buckets - 1;
        }
        var index = Array.BinarySearch(s_edges, ns);
        return index >= 0 ? index : ~index - 1;
    }

    /// <summary>The lower edge of a bucket in milliseconds; for the first bucket 1 ms, though it also holds smaller values.</summary>
    public static double LowerMs(int index) => s_edges[index] / 1e6;

    public static double UpperMs(int index) => s_edges[index + 1] / 1e6;

    /// <summary>The value at <paramref name="percentile"/> (0 to 100) in milliseconds, or null when empty.</summary>
    public static double? PercentileMs(ReadOnlySpan<long> counts, double percentile)
    {
        long total = 0;
        foreach (var count in counts)
        {
            total += count;
        }
        if (total == 0)
        {
            return null;
        }
        var rank = Math.Max(1, (long)Math.Ceiling(percentile / 100 * total));
        long seen = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            seen += counts[i];
            if (seen >= rank)
            {
                return Math.Sqrt(LowerMs(i) * UpperMs(i));
            }
        }
        return Math.Sqrt(LowerMs(Buckets - 1) * UpperMs(Buckets - 1));
    }
}
