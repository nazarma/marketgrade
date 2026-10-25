using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine.Books;

/// <summary>
/// One side of a book as two parallel arrays, best level first. Bid prices are stored negated, so both
/// sides are sorted ascending and share one binary search. Inserting and deleting shift the arrays;
/// nothing allocates unless the side is full, in which case it doubles and logs a warning.
/// </summary>
public sealed class OrderBookSide
{
    private readonly bool _bids;
    private readonly string _exchange;
    private readonly ILogger _logger;
    private long[] _keys;
    private long[] _amounts;

    public OrderBookSide(bool bids, int capacity, string exchange = "", ILogger? logger = null)
    {
        _bids = bids;
        _exchange = exchange;
        _logger = logger ?? NullLogger.Instance;
        _keys = new long[capacity];
        _amounts = new long[capacity];
    }

    public bool IsBids => _bids;

    public int Count { get; private set; }

    /// <summary>The most levels this side ever held, for tuning the capacity.</summary>
    public int MaxCount { get; private set; }

    public int Capacity => _keys.Length;

    public long PriceAt(int index) => _bids ? -_keys[index] : _keys[index];

    public long AmountAt(int index) => _amounts[index];

    /// <summary>Sets the amount at a price; 0 deletes the level. Deleting a level that is not there does nothing.</summary>
    public void Set(long price, long amount)
    {
        var key = _bids ? -price : price;
        var index = _keys.AsSpan(0, Count).BinarySearch(key);
        if (index >= 0)
        {
            if (amount != 0)
            {
                _amounts[index] = amount;
            }
            else
            {
                Count--;
                Array.Copy(_keys, index + 1, _keys, index, Count - index);
                Array.Copy(_amounts, index + 1, _amounts, index, Count - index);
            }
            return;
        }
        if (amount == 0)
        {
            return;
        }

        index = ~index;
        if (Count == _keys.Length)
        {
            Grow();
        }
        Array.Copy(_keys, index, _keys, index + 1, Count - index);
        Array.Copy(_amounts, index, _amounts, index + 1, Count - index);
        _keys[index] = key;
        _amounts[index] = amount;
        Count++;
        if (Count > MaxCount)
        {
            MaxCount = Count;
        }
    }

    /// <summary>Keeps only the best <paramref name="depth"/> levels.</summary>
    public void Truncate(int depth) => Count = Math.Min(Count, depth);

    public void Clear() => Count = 0;

    private void Grow()
    {
        var capacity = _keys.Length * 2;
        Array.Resize(ref _keys, capacity);
        Array.Resize(ref _amounts, capacity);
        _logger.LogWarning("{Exchange}: {Side} side of the book is full, grew from {Old} to {New} levels",
            _exchange, _bids ? "bid" : "ask", capacity / 2, capacity);
    }
}
