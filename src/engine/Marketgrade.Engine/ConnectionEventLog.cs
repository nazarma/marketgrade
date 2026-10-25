namespace Marketgrade.Engine;

public sealed record ConnectionEvent(DateTime TimeUtc, string Exchange, string Event, string? Detail);

/// <summary>The most recent connection events of all exchanges, oldest first.</summary>
public sealed class ConnectionEventLog(int capacity)
{
    private readonly Lock _lock = new();
    private readonly Queue<ConnectionEvent> _events = new(capacity);

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
    }

    public IReadOnlyList<ConnectionEvent> GetRecent(int count)
    {
        lock (_lock)
        {
            return _events.Skip(Math.Max(0, _events.Count - count)).ToArray();
        }
    }
}
