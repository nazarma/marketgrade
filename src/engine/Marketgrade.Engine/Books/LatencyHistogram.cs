using System.Numerics;

namespace Marketgrade.Engine.Books;

/// <summary>
/// Counts non-negative values (nanoseconds) in log-linear buckets: exact below 64, then 64 buckets per
/// power of two, so a percentile is within 1.6 % of the true value. The arrays are allocated once;
/// recording and reading percentiles never allocate. Not thread-safe.
/// </summary>
public sealed class LatencyHistogram
{
    private const int SubBuckets = 64;
    private const int SubBucketBits = 6;

    /// <summary>Larger values (about 18 minutes in nanoseconds) count in the last bucket.</summary>
    public const long MaxValue = (1L << 40) - 1;

    private static readonly int s_bucketCount = IndexOf(MaxValue) + 1;

    private readonly long[] _counts = new long[s_bucketCount];

    public long Count { get; private set; }

    /// <summary>The largest value recorded (after <see cref="MaxValue"/> limiting), exact; 0 when empty.</summary>
    public long Max { get; private set; }

    /// <summary>Values below zero, recorded as zero. For delays they mean the clocks disagree.</summary>
    public long Negative { get; private set; }

    public void Record(long value)
    {
        if (value < 0)
        {
            Negative++;
            value = 0;
        }
        value = Math.Min(value, MaxValue);
        _counts[IndexOf(value)]++;
        Count++;
        if (value > Max)
        {
            Max = value;
        }
    }

    public void Clear()
    {
        Array.Clear(_counts);
        Count = 0;
        Max = 0;
        Negative = 0;
    }

    public void Add(LatencyHistogram other)
    {
        for (var i = 0; i < _counts.Length; i++)
        {
            _counts[i] += other._counts[i];
        }
        Count += other.Count;
        Max = Math.Max(Max, other.Max);
        Negative += other.Negative;
    }

    /// <summary>Adds every value to <paramref name="coarse"/> (<see cref="CoarseHistogram.Buckets"/> counts), by the middle of its bucket.</summary>
    public void AddTo(Span<long> coarse)
    {
        for (var i = 0; i < _counts.Length; i++)
        {
            if (_counts[i] != 0)
            {
                coarse[CoarseHistogram.IndexOf(MidpointOf(i))] += _counts[i];
            }
        }
    }

    /// <summary>The value at <paramref name="percentile"/> (0 to 100): the middle of its bucket, or 0 when empty.</summary>
    public long Percentile(double percentile)
    {
        if (Count == 0)
        {
            return 0;
        }
        var rank = Math.Max(1, (long)Math.Ceiling(percentile / 100 * Count));
        long seen = 0;
        for (var i = 0; i < _counts.Length; i++)
        {
            seen += _counts[i];
            if (seen >= rank)
            {
                return MidpointOf(i);
            }
        }
        return MidpointOf(_counts.Length - 1);
    }

    private static int IndexOf(long value)
    {
        if (value < SubBuckets)
        {
            return (int)value;
        }
        var power = 63 - BitOperations.LeadingZeroCount((ulong)value);
        var shift = power - SubBucketBits;
        return SubBuckets + shift * SubBuckets + (int)((value >> shift) - SubBuckets);
    }

    private static long MidpointOf(int index)
    {
        if (index < SubBuckets)
        {
            return index;
        }
        var shift = (index - SubBuckets) / SubBuckets;
        var sub = (index - SubBuckets) % SubBuckets;
        var lower = (long)(SubBuckets + sub) << shift;
        return lower + ((1L << shift) >> 1);
    }
}

/// <summary>
/// A <see cref="LatencyHistogram"/> over roughly the last minute: a ring of slots, of which the oldest is
/// cleared and reused when its time is up. With 6 slots of 10 seconds the window covers 50 to 60 seconds.
/// </summary>
public sealed class WindowedHistogram
{
    private readonly LatencyHistogram[] _slots;
    private readonly LatencyHistogram _merged = new();
    private readonly long _slotTicks;
    private int _current;
    private long _slotStart;

    /// <param name="slotTicks">Length of one slot in the units of the timestamps passed to <see cref="Rotate"/>.</param>
    public WindowedHistogram(int slots, long slotTicks, long now)
    {
        _slots = new LatencyHistogram[slots];
        for (var i = 0; i < slots; i++)
        {
            _slots[i] = new LatencyHistogram();
        }
        _slotTicks = slotTicks;
        _slotStart = now;
    }

    public void Record(long value) => _slots[_current].Record(value);

    /// <summary>Moves to the next slot for every slot length passed since the current one started.</summary>
    public void Rotate(long now)
    {
        for (var moved = 0; now - _slotStart >= _slotTicks; moved++)
        {
            _slotStart += _slotTicks;
            if (moved < _slots.Length)
            {
                _current = (_current + 1) % _slots.Length;
                _slots[_current].Clear();
            }
        }
    }

    /// <summary>All slots merged; valid until the next call.</summary>
    public LatencyHistogram Merge()
    {
        _merged.Clear();
        foreach (var slot in _slots)
        {
            _merged.Add(slot);
        }
        return _merged;
    }
}
