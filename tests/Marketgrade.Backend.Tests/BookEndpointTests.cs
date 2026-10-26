using System.Net;
using Marketgrade.Engine.Books;

namespace Marketgrade.Backend.Tests;

public class BookEndpointTests(BackendFactory factory) : IClassFixture<BackendFactory>
{
    [Fact]
    public async Task Book_without_token_returns_401()
    {
        var response = await factory.CreateClient().GetAsync("/books/kraken", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_exchange_returns_404()
    {
        var response = await factory.CreateAuthorizedClient().GetAsync("/books/nope", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Levels_out_of_range_return_400(int levels)
    {
        var response = await factory.CreateAuthorizedClient().GetAsync($"/books/kraken?levels={levels}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Book_before_the_first_copy_returns_503()
    {
        // The engine is disabled in tests, so no book is ever published.
        var response = await factory.CreateAuthorizedClient().GetAsync("/books/Kraken?levels=5", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void Response_formats_the_requested_levels_with_the_pair_decimals()
    {
        var snapshot = new BookSnapshot("Binance", "BTCUSDT", BookState.Valid, new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc),
            new PairRules(3, "BTCUSDT", 2, 5), [8000012, 8000000], [150000, 1], [8000013], [42]);

        var response = BookResponse.From(snapshot, levels: 1);

        Assert.Equal([["80000.12", "1.50000"]], response.Bids);
        Assert.Equal([["80000.13", "0.00042"]], response.Asks);
        Assert.Equal(BookState.Valid, response.State);
    }
}
