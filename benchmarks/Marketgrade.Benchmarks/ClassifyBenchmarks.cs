using BenchmarkDotNet.Attributes;
using Marketgrade.Engine;

namespace Marketgrade.Benchmarks;

[MemoryDiagnoser]
public class ClassifyBenchmarks
{
    private byte[] _message = [];
    private ExchangeAdapter _adapter = null!;

    [Params(
        "kraken/data-book-update.json",
        "kraken/data-book-snapshot.json",
        "kraken/heartbeat.json",
        "kraken/trade-update.json",
        "coinbase/data-l2-update.json",
        "coinbase/data-l2-snapshot-trimmed.json",
        "coinbase/heartbeat.json",
        "coinbase/trade-update.json",
        "binance/data-depth-update.json",
        "binance/trade-taker-buy.json")]
    public string Fixture { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _message = Fixtures.Read(Fixture);
        _adapter = Fixtures.AdapterFor(Fixture);
    }

    [Benchmark]
    public MessageKind Classify() => _adapter.Classify(_message);
}
