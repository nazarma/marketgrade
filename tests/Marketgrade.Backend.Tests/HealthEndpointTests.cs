using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Marketgrade.Engine;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Marketgrade.Backend.Tests;

public class HealthEndpointTests : IClassFixture<HealthEndpointTests.BackendFactory>
{
    private const string Token = "test-token";

    private readonly BackendFactory _factory;

    public HealthEndpointTests(BackendFactory factory) => _factory = factory;

    [Fact]
    public async Task Health_without_token_returns_401()
    {
        var response = await _factory.CreateClient().GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_with_wrong_token_returns_401()
    {
        var response = await SendHealth("wrong-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_with_correct_token_returns_status()
    {
        var response = await SendHealth(Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var root = json.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal(EngineInfo.Version, root.GetProperty("version").GetString());
        var startedAtUtc = root.GetProperty("startedAtUtc");
        Assert.EndsWith("Z", startedAtUtc.GetString());
        Assert.Equal(DateTimeKind.Utc, startedAtUtc.GetDateTime().Kind);
    }

    private Task<HttpResponseMessage> SendHealth(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    public sealed class BackendFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder) =>
            builder.UseSetting("MARKETGRADE_TOKEN", Token);
    }
}
