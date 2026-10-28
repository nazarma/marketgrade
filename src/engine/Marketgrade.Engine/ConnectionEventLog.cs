using System.Threading.Channels;

namespace Marketgrade.Engine;

public sealed record ConnectionEvent(DateTime TimeUtc, string Exchange, string Event, string? Detail);

/// <summary>
/// The most recent connection, book and recording events of all exchanges, oldest first. Each new event is
/// also queued for one reader that pushes them on (the queue keeps the newest <c>capacity</c> events).
/// </summary>
public sealed class ConnectionEventLog(int capacity)
{
    private readonly Lock _lock = new();
    private readonly Queue<ConnectionEvent> _events = new(capacity);
    private readonly Channel<ConnectionEvent> _added = Channel.CreateBounded<ConnectionEvent>(new BoundedChannelOptions(capacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });

    /// <summary>Every event as it is added.</summary>
    public ChannelReader<ConnectionEvent> Added => _added.Reader;

    public void Add(string exchange, string @event, string? detail = null)
    {
        var entry = new ConnectionEvent(DateTime.UtcNow, exchange, @event, detail);
        lock (_lock)
        {
            if (_events.Count == capacity)
            {
                _events.Dequeue();
            }
            _events.Enqueue(entry);
        }
        _added.Writer.TryWrite(entry);
    }

    public IReadOnlyList<ConnectionEvent> GetRecent(int count)
    {
        lock (_lock)
        {
            return _events.Skip(Math.Max(0, _events.Count - count)).ToArray();
        }
    }
}
