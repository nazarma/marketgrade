using Microsoft.AspNetCore.Mvc.Testing;

namespace Marketgrade.Backend.Tests;

public sealed class BackendFactory : WebApplicationFactory<Program>
{
    public const string Token = "test-token";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("MARKETGRADE_TOKEN", Token);
        // Tests never connect to the real exchanges.
        builder.UseSetting("MARKETGRADE_ENGINE_DISABLED", "1");
    }
}
