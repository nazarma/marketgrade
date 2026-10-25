using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine;

/// <summary>
/// Fixed set of message buffers created at startup. Renting and returning never allocate;
/// only an empty pool creates a new buffer (a pool miss).
/// </summary>
public sealed class MessageBufferPool
{
    private readonly Lock _lock = new();
    private readonly int _bufferSize;
    private readonly ILogger _logger;
    private readonly EngineMetrics? _metrics;
    private MessageBuffer[] _free;
    private int _count;
    private int _created;
    private long _misses;
    private long _lastMissWarning = LogThrottle.Never;

    public MessageBufferPool(int size, int bufferSize, ILogger? logger = null, EngineMetrics? metrics = null)
    {
        _bufferSize = bufferSize;
        _logger = logger ?? NullLogger.Instance;
        _metrics = metrics;
        _free = new MessageBuffer[size];
        for (var i = 0; i < size; i++)
        {
            _free[i] = new MessageBuffer(this, bufferSize);
        }
        _count = size;
        _created = size;
    }

    public int Available
    {
        get
        {
            lock (_lock)
            {
                return _count;
            }
        }
    }

    public int Created => Volatile.Read(ref _created);

    public long Misses => Interlocked.Read(ref _misses);

    public MessageBuffer Rent()
    {
        lock (_lock)
        {
            if (_count > 0)
            {
                var buffer = _free[--_count];
                _free[_count] = null!;
                return buffer;
            }
        }

        Interlocked.Increment(ref _created);
        Interlocked.Increment(ref _misses);
        _metrics?.PoolMisses.Add(1);
        if (LogThrottle.ShouldLog(ref _lastMissWarning))
        {
            _logger.LogWarning("Message buffer pool is empty, created a new buffer ({Created} in total)", Created);
        }
        return new MessageBuffer(this, _bufferSize);
    }

    internal void Return(MessageBuffer buffer)
    {
        buffer.ReturnLargeArray();
        lock (_lock)
        {
            if (_count == _free.Length)
            {
                Array.Resize(ref _free, _free.Length * 2);
            }
            _free[_count++] = buffer;
        }
    }
}
