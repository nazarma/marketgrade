using Marketgrade.Engine.Flow;

namespace Marketgrade.Engine.Tests.Flow;

public class FlowRingTests
{
    // Every field holds the sequence number, so a torn copy shows as fields that disagree.
    private static FlowEvent EventAt(long seq) => new(seq, FlowKind.BookChange, FlowSide.Buy, seq, seq);

    private static void WriteMany(FlowRing ring, long count)
    {
        for (var seq = ring.Published; seq < count; seq++)
        {
            ring.Write(EventAt(seq));
        }
    }

    [Fact]
    public void Reads_what_was_written_and_continues_from_next()
    {
        var ring = new FlowRing(8);
        WriteMany(ring, 5);
        var buffer = new FlowEvent[8];

        var first = ring.Read(0, buffer);
        Assert.Equal(new FlowRead(5, 5, 0), first);
        Assert.Equal([0, 1, 2, 3, 4], buffer[..5].Select(e => e.Price));

        WriteMany(ring, 7);
        var second = ring.Read(first.Next, buffer);
        Assert.Equal(new FlowRead(2, 7, 0), second);
        Assert.Equal([5, 6], buffer[..2].Select(e => e.Price));
        Assert.Equal(new FlowRead(0, 7, 0), ring.Read(second.Next, buffer));
    }

    [Fact]
    public void Wraps_around_many_times()
    {
        var ring = new FlowRing(8);
        var buffer = new FlowEvent[8];
        long next = 0;
        for (var round = 1; round <= 1_000; round++)
        {
            WriteMany(ring, round * 5L);
            var read = ring.Read(next, buffer);
            Assert.Equal(0, read.Lost);
            Assert.Equal(5, read.Count);
            Assert.Equal(Enumerable.Range(0, 5).Select(i => next + i), buffer[..5].Select(e => e.Price));
            next = read.Next;
        }
    }

    [Fact]
    public void Counts_events_overwritten_before_the_read()
    {
        var ring = new FlowRing(8);
        WriteMany(ring, 20);
        var buffer = new FlowEvent[8];

        var read = ring.Read(0, buffer);

        // Event 12 shares its slot with event 20, which the writer may already be writing.
        Assert.Equal(new FlowRead(7, 20, 13), read);
        Assert.Equal([13, 14, 15, 16, 17, 18, 19], buffer[..7].Select(e => e.Price));
    }

    [Fact]
    public void Smaller_destination_reads_the_oldest_first()
    {
        var ring = new FlowRing(8);
        WriteMany(ring, 6);
        var buffer = new FlowEvent[4];

        var read = ring.Read(0, buffer);

        Assert.Equal(new FlowRead(4, 4, 0), read);
        Assert.Equal(new FlowRead(2, 6, 0), ring.Read(read.Next, buffer));
    }

    [Fact]
    public void A_start_beyond_the_end_reads_nothing()
    {
        var ring = new FlowRing(8);
        WriteMany(ring, 3);

        Assert.Equal(new FlowRead(0, 3, 0), ring.Read(100, new FlowEvent[8]));
    }

    [Fact]
    public void Capacity_must_be_a_power_of_two()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FlowRing(100));
        Assert.Equal(FlowRing.DefaultCapacity, new FlowRing().Capacity);
    }

    [Fact]
    public void Writing_allocates_nothing()
    {
        var ring = new FlowRing(64);
        WriteMany(ring, 64);
        // A first pass lets the runtime finish compiling the loop; it can allocate while it does.
        WriteRange(ring, 64, 100_064);

        var before = GC.GetAllocatedBytesForCurrentThread();
        WriteRange(ring, 100_064, 200_064);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        static void WriteRange(FlowRing ring, long from, long to)
        {
            for (var seq = from; seq < to; seq++)
            {
                ring.Write(EventAt(seq));
            }
        }
    }

    [Fact]
    public void A_reader_on_another_thread_never_gets_a_torn_or_out_of_order_event()
    {
        var ring = new FlowRing(64);
        const long total = 2_000_000;
        var writer = new Thread(() => WriteMany(ring, total));
        writer.Start();

        var buffer = new FlowEvent[64];
        long next = 0, copied = 0, lost = 0;
        while (next < total)
        {
            var read = ring.Read(next, buffer);
            for (var i = 0; i < read.Count; i++)
            {
                var expected = read.Next - read.Count + i;
                Assert.Equal(new FlowEvent(expected, FlowKind.BookChange, FlowSide.Buy, expected, expected), buffer[i]);
            }
            copied += read.Count;
            lost += read.Lost;
            next = read.Next;
        }
        writer.Join();

        Assert.Equal(total, copied + lost);
    }
}
