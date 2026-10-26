using Marketgrade.Engine;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Recording;

namespace Marketgrade.Benchmarks;

/// <summary>Real exchange messages captured by the probe tool, shared with the engine tests.</summary>
internal static class Fixtures
{
    public static byte[] Read(string exchangeAndFile) => File.ReadAllBytes(PathOf(exchangeAndFile));

    /// <summary>The records of a trimmed real recording in <c>Fixtures/replay</c>.</summary>
    public static List<(RecordHeader Header, byte[] Payload)> Records(string exchange)
    {
        using var reader = RecordingReader.Open(PathOf($"replay/{exchange}.mgrec.zst"));
        var records = new List<(RecordHeader, byte[])>();
        while (reader.Read())
        {
            records.Add((reader.Current, reader.Payload.ToArray()));
        }
        return records;
    }

    /// <summary>The rules the app fetched for each pair when the fixtures were recorded.</summary>
    public static PairRules RulesFor(string exchange) => exchange switch
    {
        "kraken" => new PairRules(1, "BTC/USD", 1, 8),
        "coinbase" => new PairRules(2, "BTC-USD", 2, 8),
        "binance" => new PairRules(3, "BTCUSDT", 2, 5),
        var other => throw new ArgumentException($"Unknown exchange {other}"),
    };

    private static string PathOf(string exchangeAndFile)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "Marketgrade.slnx")))
        {
            directory = directory.Parent ?? throw new DirectoryNotFoundException("Repository root not found");
        }
        return Path.Combine(directory.FullName, "tests", "Marketgrade.Engine.Tests", "Fixtures", exchangeAndFile);
    }

    public static ExchangeAdapter AdapterFor(string exchangeAndFile) => exchangeAndFile.Split('/')[0] switch
    {
        "kraken" => new KrakenAdapter(),
        "coinbase" => new CoinbaseAdapter(),
        "binance" => new BinanceAdapter(),
        var other => throw new ArgumentException($"Unknown exchange {other}"),
    };
}
