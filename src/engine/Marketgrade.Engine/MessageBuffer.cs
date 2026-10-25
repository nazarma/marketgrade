using System.Buffers;
using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine;

/// <summary>
/// One raw exchange message, shared by all consumers of an exchange. Each consumer calls
/// <see cref="Release"/> exactly once, as the last thing it does with the buffer.
/// </summary>
public sealed class MessageBuffer
{
    public byte[] Bytes;
    public int Length;
    public int ExchangeId;
    public long ConnectionId;
    public long Seq;
    public long ArrivalUnixNs;
    public int Counter;

    /// <summary>Anything but <see cref="RecordType.Message"/> goes to the recorder only.</summary>
    public RecordType Type = RecordType.Message;

    /// <summary>Messages dropped for the recorder right before this one, stamped by the connection.</summary>
    public long GapFirstSeq;
    public long GapLastSeq;
    public long GapCount;

    private readonly byte[] _standard;
    private readonly MessageBufferPool _pool;

    internal MessageBuffer(MessageBufferPool pool, int size)
    {
        _pool = pool;
        _standard = new byte[size];
        Bytes = _standard;
    }

    public ReadOnlySpan<byte> Span => Bytes.AsSpan(0, Length);

    public void Release()
    {
        if (Interlocked.Decrement(ref Counter) == 0)
        {
            Length = 0;
            Type = RecordType.Message;
            GapFirstSeq = 0;
            GapLastSeq = 0;
            GapCount = 0;
            _pool.Return(this);
        }
    }

    /// <summary>Replaces <see cref="Bytes"/> with an array twice as large, keeping the first <paramref name="received"/> bytes.</summary>
    internal void Grow(int received)
    {
        var larger = ArrayPool<byte>.Shared.Rent(Bytes.Length * 2);
        Bytes.AsSpan(0, received).CopyTo(larger);
        ReturnLargeArray();
        Bytes = larger;
    }

    /// <summary>Gives a large array back to <see cref="ArrayPool{T}"/> and restores the standard array.</summary>
    internal void ReturnLargeArray()
    {
        if (Bytes != _standard)
        {
            ArrayPool<byte>.Shared.Return(Bytes);
            Bytes = _standard;
        }
    }
}
