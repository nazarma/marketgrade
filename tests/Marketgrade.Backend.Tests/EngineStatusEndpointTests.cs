using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Marketgrade.Backend.Tests;

public class EngineStatusEndpointTests(BackendFactory factory) : IClassFixture<BackendFactory>
{
    [Fact]
    public async Task Engine_status_without_token_returns_401()
    {
        var response = await factory.CreateClient().GetAsync("/engine/status", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Engine_status_lists_every_exchange_and_the_recent_events()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/engine/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", BackendFactory.Token);

        var response = await factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var exchanges = json.RootElement.GetProperty("status").GetProperty("exchanges").EnumerateArray().ToList();
        Assert.Equal(["Kraken", "Coinbase", "Binance"], exchanges.Select(e => e.GetProperty("name").GetString()));
        Assert.Equal(["BTC/USD", "BTC-USD", "BTCUSDT"], exchanges.Select(e => e.GetProperty("pair").GetString()));
        Assert.All(exchanges, e => Assert.Equal("Stopped", e.GetProperty("state").GetString()));
        Assert.All(exchanges, e => Assert.Equal(0, e.GetProperty("messagesPerSecond").GetDouble()));
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("events").ValueKind);
    }
}
