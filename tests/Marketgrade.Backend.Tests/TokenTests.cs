using System.Net;

namespace Marketgrade.Backend.Tests;

/// <summary>The token in the query string is only for the live hub, whose WebSocket cannot carry a header.</summary>
public class TokenTests(BackendFactory factory) : IClassFixture<BackendFactory>
{
    private async Task<HttpStatusCode> PostAsync(string path)
    {
        var response = await factory.CreateClient().PostAsync(path, null, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private async Task<HttpStatusCode> GetAsync(string path)
    {
        var response = await factory.CreateClient().GetAsync(path, TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    [Fact]
    public async Task Query_token_is_accepted_on_the_live_hub()
    {
        Assert.NotEqual(HttpStatusCode.Unauthorized, await PostAsync($"/hubs/live/negotiate?negotiateVersion=1&access_token={BackendFactory.Token}"));
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/engine/status")]
    [InlineData("/settings/recording")]
    [InlineData("/hubs/livex")]
    [InlineData("/history/delay")]
    public async Task Query_token_is_rejected_everywhere_else(string path)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync($"{path}?access_token={BackendFactory.Token}"));
    }

    [Fact]
    public async Task Query_token_never_reaches_the_logs()
    {
        await PostAsync($"/hubs/live/negotiate?negotiateVersion=1&access_token={BackendFactory.Token}");
        await GetAsync($"/hubs/live?id=nope&access_token={BackendFactory.Token}");
        await GetAsync($"/engine/status?access_token={BackendFactory.Token}");

        Assert.NotEmpty(factory.Logs);
        Assert.DoesNotContain(factory.Logs, line => line.Contains(BackendFactory.Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wrong_query_token_is_rejected_on_the_live_hub()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/hubs/live/negotiate?negotiateVersion=1&access_token=wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/hubs/live/negotiate?negotiateVersion=1"));
    }
}
