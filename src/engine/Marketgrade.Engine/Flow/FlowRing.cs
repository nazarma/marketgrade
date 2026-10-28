using System.Numerics;

namespace Marketgrade.Engine.Flow;

/// <summary>The outcome of <see cref="FlowRing.Read"/>.</summary>
/// <param name="Count">Events copied, oldest first.</param>
/// <param name="Next">The sequence number to read from next time.</param>
/// <param name="Lost">Events after the requested start that were overwritten before they could be copied.</param>
public readonly record struct FlowRead(int Count, long Next, long Lost);

/// <summary>
/// The latest flow events of one exchange in a preallocated ring, written by the book worker and copied by a
/// reader on another thread. Every event has a sequence number (0, 1, 2, ...); the writer publishes the
/// number of events written after each one, so a reader can copy a range and tell which events the writer
/// may have overwritten meanwhile. Writing never allocates and never waits for a reader.
/// </summary>
public sealed class FlowRing
{
    public const int DefaultCapacity = 4_096;

    private readonly FlowEvent[] _events;
    private readonly int _mask;
    private long _published;

    public FlowRing(int capacity = DefaultCapacity)
    {
        if (capacity < 2 || !BitOperations.IsPow2(capacity))
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Must be a power of two");
        }
        _events = new FlowEvent[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _events.Length;

    /// <summary>Events written so far, which is also the sequence number of the next one.</summary>
    public long Published => Interlocked.Read(ref _published);

    /// <summary>Single writer only.</summary>
    public void Write(in FlowEvent flowEvent)
    {
        var seq = _published;
        _events[seq & _mask] = flowEvent;
        // A full barrier: the event is visible before its number, and the number before the next event's
        // stores, so a reader that sees N published knows that only the slot of event N may be changing.
        Interlocked.Exchange(ref _published, seq + 1);
    }

    /// <summary>
    /// Copies events from sequence number <paramref name="from"/> on, oldest first, as many as fit in
    /// <paramref name="destination"/>. Events overwritten before or during the copy are left out and counted.
    /// </summary>
    public FlowRead Read(long from, Span<FlowEvent> destination)
    {
        var end = Interlocked.Read(ref _published);
        from = Math.Clamp(from, 0, end);
        var start = Math.Max(from, end - _events.Length);
        var count = (int)Math.Min(end - start, destination.Length);
        for (var i = 0; i < count; i++)
        {
            destination[i] = _events[(start + i) & _mask];
        }

        // The writer may have moved on while we copied: with N published it may be writing event N, which
        // reuses the slot of event N - capacity, so only events after that one are certainly intact.
        Interlocked.MemoryBarrier();
        var firstIntact = Interlocked.Read(ref _published) - _events.Length + 1;
        var torn = (int)Math.Clamp(firstIntact - start, 0, count);
        if (torn > 0)
        {
            destination[torn..count].CopyTo(destination);
            count -= torn;
            start += torn;
        }
        return new FlowRead(count, start + count, start - from);
    }
}
