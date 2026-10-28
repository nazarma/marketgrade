using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine.Books;

/// <summary>
/// One side of a book as two parallel arrays sorted ascending, with the best level at the end: almost all
/// changes are near the best price, so inserting and deleting by shifting moves only the few levels
/// above it. Ask prices are stored negated, which puts the lowest ask last and lets both sides share
/// one binary search. Index 0 of <see cref="PriceAt"/> is always the best level. Nothing allocates
/// unless the side is full, in which case it doubles and logs a warning.
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

    /// <summary>The price of the level <paramref name="index"/> steps from the best one.</summary>
    public long PriceAt(int index) => _bids ? _keys[Count - 1 - index] : -_keys[Count - 1 - index];

    public long AmountAt(int index) => _amounts[Count - 1 - index];

    /// <summary>Sets the amount at a price; 0 deletes the level. Deleting a level that is not there does nothing.</summary>
    /// <returns>The amount the level had before, 0 if there was none.</returns>
    public long Set(long price, long amount)
    {
        var key = _bids ? price : -price;
        var index = _keys.AsSpan(0, Count).BinarySearch(key);
        if (index >= 0)
        {
            var previous = _amounts[index];
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
            return previous;
        }
        if (amount == 0)
        {
            return 0;
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
        return 0;
    }

    /// <summary>Keeps only the best <paramref name="depth"/> levels.</summary>
    public void Truncate(int depth)
    {
        if (Count <= depth)
        {
            return;
        }
        var drop = Count - depth;
        Array.Copy(_keys, drop, _keys, 0, depth);
        Array.Copy(_amounts, drop, _amounts, 0, depth);
        Count = depth;
    }

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
