using Marketgrade.Engine.Books;

namespace Marketgrade.Engine.Tests.Books;

public class LatencyHistogramTests
{
    [Fact]
    public void Max_is_exact_and_survives_merging()
    {
        var first = new LatencyHistogram();
        var second = new LatencyHistogram();
        first.Record(123_456_789);
        first.Record(7);
        second.Record(987_654_321);
        second.Record(-5);

        first.Add(second);

        Assert.Equal(987_654_321, first.Max);
        first.Clear();
        Assert.Equal(0, first.Max);
        Assert.Equal(LatencyHistogram.MaxValue, Record(long.MaxValue).Max);
    }

    private static LatencyHistogram Record(params long[] values)
    {
        var histogram = new LatencyHistogram();
        foreach (var value in values)
        {
            histogram.Record(value);
        }
        return histogram;
    }

    [Fact]
    public void Small_values_are_exact()
    {
        var histogram = new LatencyHistogram();
        foreach (var value in new long[] { 5, 1, 3, 2, 4 })
        {
            histogram.Record(value);
        }

        Assert.Equal(3, histogram.Percentile(50));
        Assert.Equal(5, histogram.Percentile(99));
        Assert.Equal(1, histogram.Percentile(0));
    }

    [Theory]
    [InlineData(50, 500_000)]
    [InlineData(99, 990_000)]
    [InlineData(99.9, 999_000)]
    public void Percentiles_of_uniform_data_are_within_bucket_precision(double percentile, long expected)
    {
        var histogram = new LatencyHistogram();
        for (long value = 1_000; value <= 1_000_000; value += 1_000)
        {
            histogram.Record(value);
        }

        Assert.InRange(histogram.Percentile(percentile), expected * 0.984, expected * 1.016);
    }

    [Fact]
    public void Long_tail_shows_in_the_99th_percentile_only()
    {
        // 98 % around 2 ms, 2 % at 250 ms: the median stays at 2 ms, the 99th percentile is the tail.
        var histogram = new LatencyHistogram();
        for (var i = 0; i < 9_800; i++)
        {
            histogram.Record(2_000_000);
        }
        for (var i = 0; i < 200; i++)
        {
            histogram.Record(250_000_000);
        }

        Assert.InRange(histogram.Percentile(50), 1_970_000, 2_030_000);
        Assert.InRange(histogram.Percentile(99), 246_000_000, 254_000_000);
    }

    [Fact]
    public void Negative_and_huge_values_are_clamped_and_counted()
    {
        var histogram = new LatencyHistogram();
        histogram.Record(-5);
        histogram.Record(long.MaxValue);

        Assert.Equal(2, histogram.Count);
        Assert.Equal(1, histogram.Negative);
        Assert.Equal(0, histogram.Percentile(50));
        Assert.InRange(histogram.Percentile(100), LatencyHistogram.MaxValue * 0.984, LatencyHistogram.MaxValue);
    }

    [Fact]
    public void Empty_histogram_reports_zero()
    {
        Assert.Equal(0, new LatencyHistogram().Percentile(50));
    }

    [Fact]
    public void Window_forgets_values_older_than_its_slots()
    {
        var window = new WindowedHistogram(slots: 6, slotTicks: 10, now: 0);
        window.Record(1_000);
        window.Rotate(25);
        window.Record(50);

        var merged = window.Merge();
        Assert.Equal(2, merged.Count);

        // Slot of the 1,000 started at 0; it is reused once 6 slots have passed.
        window.Rotate(60);
        merged = window.Merge();
        Assert.Equal(1, merged.Count);
        Assert.Equal(50, merged.Percentile(50));

        // A long quiet period clears everything.
        window.Rotate(10_000);
        Assert.Equal(0, window.Merge().Count);
        window.Record(7);
        Assert.Equal(7, window.Merge().Percentile(99));
    }
}
