using System.Net;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;

namespace Marketgrade.Engine.Tests.Books;

/// <summary>Pair rules from real responses captured on 2026-10-25, served by a stub handler.</summary>
public class PairRulesClientTests
{
    public static TheoryData<string, string, int, int> Exchanges() => new()
    {
        { "kraken", "https://api.kraken.com/0/public/AssetPairs?pair=XBTUSD", 1, 8 },
        { "coinbase", "https://api.coinbase.com/api/v3/brokerage/market/products/BTC-USD", 2, 8 },
        { "binance", "https://data-api.binance.vision/api/v3/exchangeInfo?symbol=BTCUSDT", 2, 5 },
    };

    [Theory]
    [MemberData(nameof(Exchanges))]
    public async Task Reads_decimals_from_the_exchange_response(string exchange, string url, int priceDecimals, int amountDecimals)
    {
        var handler = new StubHttpHandler(HttpStatusCode.OK, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "pair-rules", $"{exchange}.json")));
        using var http = new HttpClient(handler);
        var adapter = Adapter(exchange);

        var rules = await PairRulesClient.FetchAsync(http, adapter, TestContext.Current.CancellationToken);

        Assert.Equal(url, handler.LastUri?.AbsoluteUri);
        Assert.Equal(new PairRules(adapter.Id, adapter.Pair, priceDecimals, amountDecimals), rules);
    }

    [Fact]
    public async Task Http_error_fails_the_fetch()
    {
        using var http = new HttpClient(new StubHttpHandler(HttpStatusCode.ServiceUnavailable, []));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            PairRulesClient.FetchAsync(http, new CoinbaseAdapter(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Kraken_error_list_fails_the_fetch()
    {
        using var http = new HttpClient(new StubHttpHandler(HttpStatusCode.OK, """{"error":["EQuery:Unknown asset pair"]}"""u8.ToArray()));

        await Assert.ThrowsAsync<FormatException>(() =>
            PairRulesClient.FetchAsync(http, new KrakenAdapter(), TestContext.Current.CancellationToken));
    }

    private static ExchangeAdapter Adapter(string exchange) => exchange switch
    {
        "kraken" => new KrakenAdapter(),
        "coinbase" => new CoinbaseAdapter(),
        _ => new BinanceAdapter(),
    };
}
