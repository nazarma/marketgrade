using BenchmarkDotNet.Attributes;
using Marketgrade.Engine;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Flow;

namespace Marketgrade.Benchmarks;

/// <summary>
/// One real trade message through its exchange's handler: classify, parse and write to the flow ring. Each
/// call starts a new connection, so Coinbase's first sequence number is always accepted and checked.
/// </summary>
[MemoryDiagnoser]
public class TradeBenchmarks
{
    private readonly MessageBufferPool _pool = new(4, 64 * 1024);
    private BookHandler _handler = null!;
    private MessageBuffer _message = null!;

    [Params("kraken/trade-update.json", "coinbase/trade-update.json", "binance/trade-taker-buy.json")]
    public string Message { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        var exchange = Message.Split('/')[0];
        _handler = Fixtures.AdapterFor(Message).CreateBookHandler(Fixtures.RulesFor(exchange), new EngineOptions())!;
        _handler.Flow = new FlowRing();
        var bytes = Fixtures.Read(Message);
        _message = _pool.Rent();
        bytes.CopyTo(_message.Bytes, 0);
        _message.Length = bytes.Length;
        _message.Counter = 1;
    }

    [Benchmark]
    public int Handle()
    {
        _message.ConnectionId++;
        _handler.Handle(_message);
        return _handler.Trades;
    }
}
