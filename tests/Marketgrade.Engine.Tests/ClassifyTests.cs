using Marketgrade.Engine.Exchanges;

namespace Marketgrade.Engine.Tests;

/// <summary>
/// Classifies real messages captured with the probe tool (<c>--capture</c>). Each fixture lives in
/// <c>Fixtures/&lt;exchange&gt;/</c> and its file name starts with the expected kind.
/// </summary>
public class ClassifyTests
{
    private static readonly Dictionary<string, ExchangeAdapter> s_adapters = new()
    {
        ["kraken"] = new KrakenAdapter(),
        ["coinbase"] = new CoinbaseAdapter(),
        ["binance"] = new BinanceAdapter(),
    };

    public static TheoryData<string, string> Fixtures()
    {
        var data = new TheoryData<string, string>();
        foreach (var exchange in s_adapters.Keys)
        {
            foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", exchange)).Order())
            {
                data.Add(exchange, Path.GetFileName(file));
            }
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Classifies_captured_message(string exchange, string file)
    {
        var message = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", exchange, file));
        var expected = Enum.Parse<MessageKind>(file.Split('-', '.')[0], ignoreCase: true);

        Assert.Equal(expected, s_adapters[exchange].Classify(message));
    }

    [Fact]
    public void Every_exchange_has_a_fixture_of_each_kind_it_sends()
    {
        string[] kinds(string exchange) =>
            [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", exchange))
                .Select(f => Path.GetFileName(f).Split('-', '.')[0]).Distinct().Order()];

        Assert.Equal(["control", "data", "error", "heartbeat", "trade"], kinds("kraken"));
        Assert.Equal(["control", "data", "error", "heartbeat", "trade"], kinds("coinbase"));
        Assert.Equal(["control", "data", "error", "trade"], kinds("binance"));
    }

    [Theory]
    [InlineData("kraken")]
    [InlineData("coinbase")]
    [InlineData("binance")]
    public void Malformed_message_is_an_error(string exchange)
    {
        var adapter = s_adapters[exchange];

        Assert.Equal(MessageKind.Error, adapter.Classify("{\"channel\":"u8));
        Assert.Equal(MessageKind.Error, adapter.Classify("not json"u8));
        Assert.Equal(MessageKind.Error, adapter.Classify([]));
    }

    [Fact]
    public void Binance_server_shutdown_notice_is_control()
    {
        Assert.Equal(MessageKind.Control, new BinanceAdapter().Classify("""{"e":"serverShutdown","E":1781596800000}"""u8));
    }
}
