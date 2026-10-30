using System.Globalization;
using System.Text.Json;

namespace Marketgrade.Backend.Tests;

/// <summary>
/// The backend runs without culture data, so numbers and dates format the same on every computer. The test
/// projects run the same way (Directory.Build.props), so every other test checks formatting as it ships.
/// </summary>
public class GlobalizationTests
{
    [Fact]
    public void Backend_ships_with_invariant_globalization()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Marketgrade.Backend.runtimeconfig.json");
        using var config = JsonDocument.Parse(File.ReadAllText(path));

        var invariant = config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties")
            .GetProperty("System.Globalization.Invariant");

        Assert.True(invariant.GetBoolean());
    }

    [Fact]
    public void Tests_run_with_the_invariant_culture()
    {
        Assert.Equal(CultureInfo.InvariantCulture.Name, CultureInfo.CurrentCulture.Name);
        Assert.Equal("1234.5", $"{1234.5:0.#}");
        Assert.Equal("2026-10-30T12:00:00.0000000Z", new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc).ToString("O"));
    }
}
