using BenchmarkDotNet.Attributes;
using Marketgrade.Engine;

namespace Marketgrade.Benchmarks;

[MemoryDiagnoser]
public class BufferCycleBenchmarks
{
    private readonly MessageBufferPool _pool = new(1_024, 16 * 1024);
    private readonly byte[] _message = new byte[1024];
    private long _seq;

    [Benchmark]
    public void RentFillRelease()
    {
        var buffer = _pool.Rent();
        _message.CopyTo(buffer.Bytes, 0);
        buffer.Length = _message.Length;
        buffer.ExchangeId = 1;
        buffer.ConnectionId = 1;
        buffer.Seq = ++_seq;
        buffer.ArrivalUnixNs = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;
        buffer.Counter = 1;
        buffer.Release();
    }
}
