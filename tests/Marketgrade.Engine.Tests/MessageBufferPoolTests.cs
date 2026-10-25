namespace Marketgrade.Engine.Tests;

public class MessageBufferPoolTests
{
    [Fact]
    public void Released_buffer_returns_to_the_pool_empty()
    {
        var pool = new MessageBufferPool(4, 1024);

        var buffer = pool.Rent();
        buffer.Length = 10;
        buffer.Counter = 1;
        Assert.Equal(3, pool.Available);

        buffer.Release();

        Assert.Equal(4, pool.Available);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void Buffer_returns_only_after_every_consumer_released_it()
    {
        var pool = new MessageBufferPool(1, 1024);
        var buffer = pool.Rent();
        buffer.Length = 1;
        buffer.Counter = 2;

        buffer.Release();
        Assert.Equal(0, pool.Available);
        Assert.Equal(1, buffer.Length);

        buffer.Release();
        Assert.Equal(1, pool.Available);
    }

    [Fact]
    public void Empty_pool_creates_a_new_buffer_and_counts_a_miss()
    {
        var pool = new MessageBufferPool(1, 1024);
        var first = pool.Rent();

        var second = pool.Rent();

        Assert.NotSame(first, second);
        Assert.Equal(1, pool.Misses);
        Assert.Equal(2, pool.Created);

        first.Counter = 1;
        second.Counter = 1;
        first.Release();
        second.Release();
        Assert.Equal(2, pool.Available);
    }

    [Fact]
    public void Grown_buffer_keeps_its_data_and_gets_the_standard_array_back_on_release()
    {
        var pool = new MessageBufferPool(1, 1024);
        var buffer = pool.Rent();
        var standard = buffer.Bytes;
        buffer.Bytes.AsSpan(0, 1024).Fill(7);

        buffer.Grow(1024);

        Assert.True(buffer.Bytes.Length >= 2048);
        Assert.True(buffer.Bytes.AsSpan(0, 1024).IndexOfAnyExcept((byte)7) < 0);

        buffer.Length = 1024;
        buffer.Counter = 1;
        buffer.Release();

        Assert.Same(standard, pool.Rent().Bytes);
    }
}
