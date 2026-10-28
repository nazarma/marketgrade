using BenchmarkDotNet.Attributes;
using Marketgrade.Engine.Flow;

namespace Marketgrade.Benchmarks;

/// <summary>The book worker's side of the flow ring: one event written per book change or trade.</summary>
[MemoryDiagnoser]
public class FlowRingBenchmarks
{
    private readonly FlowRing _ring = new();
    private readonly FlowEvent[] _buffer = new FlowEvent[FlowRing.DefaultCapacity];
    private long _time;
    private long _next;

    [Benchmark]
    public void Write() => _ring.Write(new FlowEvent(++_time, FlowKind.BookChange, FlowSide.Sell, 8_119_540, -2_832));

    /// <summary>What the backend does every 100 ms: copy the events since its last read.</summary>
    [Benchmark(OperationsPerInvoke = 1_000)]
    public void WriteThousandThenRead()
    {
        for (var i = 0; i < 1_000; i++)
        {
            _ring.Write(new FlowEvent(++_time, FlowKind.Trade, FlowSide.Buy, 8_119_550, 12_737));
        }
        _next = _ring.Read(_next, _buffer).Next;
    }
}
