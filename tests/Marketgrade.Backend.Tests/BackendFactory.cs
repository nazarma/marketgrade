using Microsoft.AspNetCore.Mvc.Testing;

namespace Marketgrade.Backend.Tests;

public sealed class BackendFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token";

    /// <summary>
    /// Settings database and recordings of this backend, never the real app data folder.
    /// Set it to an earlier backend's folder to simulate a restart.
    /// </summary>
    public string DataFolder { get; init; } = Path.Combine(Path.GetTempPath(), "marketgrade-tests", Guid.NewGuid().ToString("N"));

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("MARKETGRADE_TOKEN", Token);
        builder.UseSetting("MARKETGRADE_DATA_DIR", DataFolder);
        // Tests never connect to the real exchanges.
        builder.UseSetting("MARKETGRADE_ENGINE_DISABLED", "1");
    }
}
