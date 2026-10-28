using Marketgrade.Engine.Books;

namespace Marketgrade.Engine.Tests.Books;

public class CoarseHistogramTests
{
    [Fact]
    public void Buckets_cover_1_ms_to_60_s_with_equal_ratios()
    {
        Assert.Equal(1.0, CoarseHistogram.LowerMs(0));
        Assert.Equal(60_000.0, CoarseHistogram.UpperMs(CoarseHistogram.Buckets - 1), 6);
        var ratio = Math.Pow(60_000, 1.0 / CoarseHistogram.Buckets);
        for (var i = 0; i < CoarseHistogram.Buckets; i++)
        {
            Assert.Equal(ratio, CoarseHistogram.UpperMs(i) / CoarseHistogram.LowerMs(i), 3);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(999_999, 0)]
    [InlineData(1_000_000, 0)]
    [InlineData(1_258_000, 1)]
    [InlineData(59_999_999_999, CoarseHistogram.Buckets - 1)]
    [InlineData(3_600_000_000_000, CoarseHistogram.Buckets - 1)]
    public void Values_outside_the_range_count_in_the_first_or_last_bucket(long ns, int bucket)
    {
        Assert.Equal(bucket, CoarseHistogram.IndexOf(ns));
    }

    [Fact]
    public void Every_bucket_starts_at_its_lower_edge()
    {
        for (var i = 1; i < CoarseHistogram.Buckets; i++)
        {
            var lower = (long)Math.Round(CoarseHistogram.LowerMs(i) * 1e6);
            Assert.Equal(i, CoarseHistogram.IndexOf(lower));
            Assert.Equal(i - 1, CoarseHistogram.IndexOf(lower - 1));
        }
    }

    [Theory]
    [InlineData(50)]
    [InlineData(99)]
    public void Percentiles_from_summed_minutes_are_within_12_percent(double percentile)
    {
        // A day of minutes with delays spread between 20 and 400 ms, each minute added to the same counts.
        var counts = new long[CoarseHistogram.Buckets];
        var all = new List<long>();
        var random = new Random(5);
        for (var minute = 0; minute < 1_440; minute++)
        {
            var histogram = new LatencyHistogram();
            for (var i = 0; i < 50; i++)
            {
                var ns = (long)(20e6 * Math.Pow(20, random.NextDouble()));
                histogram.Record(ns);
                all.Add(ns);
            }
            histogram.AddTo(counts);
        }
        all.Sort();
        var exactMs = all[(int)Math.Ceiling(percentile / 100 * all.Count) - 1] / 1e6;

        var approximate = CoarseHistogram.PercentileMs(counts, percentile)!.Value;

        Assert.InRange(approximate / exactMs, 0.88, 1.13);
        Assert.Equal(all.Count, counts.Sum());
    }

    [Fact]
    public void Empty_counts_have_no_percentile()
    {
        Assert.Null(CoarseHistogram.PercentileMs(new long[CoarseHistogram.Buckets], 50));
    }
}
