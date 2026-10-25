using Marketgrade.Engine;
using Marketgrade.Engine.Exchanges;

namespace Marketgrade.Benchmarks;

/// <summary>Real exchange messages captured by the probe tool, shared with the engine tests.</summary>
internal static class Fixtures
{
    public static byte[] Read(string exchangeAndFile)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "Marketgrade.slnx")))
        {
            directory = directory.Parent ?? throw new DirectoryNotFoundException("Repository root not found");
        }
        return File.ReadAllBytes(Path.Combine(directory.FullName, "tests", "Marketgrade.Engine.Tests", "Fixtures", exchangeAndFile));
    }

    public static ExchangeAdapter AdapterFor(string exchangeAndFile) => exchangeAndFile.Split('/')[0] switch
    {
        "kraken" => new KrakenAdapter(),
        "coinbase" => new CoinbaseAdapter(),
        "binance" => new BinanceAdapter(),
        var other => throw new ArgumentException($"Unknown exchange {other}"),
    };
}
