using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Marketgrade.Backend.Tests;

public class SettingsEndpointTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Recording_settings_without_token_return_401()
    {
        await using var factory = new BackendFactory();

        var response = await factory.CreateClient().GetAsync("/settings/recording", Token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Fresh_install_returns_the_defaults()
    {
        await using var factory = new BackendFactory();

        var json = await factory.CreateAuthorizedClient().GetFromJsonAsync<JsonObject>("/settings/recording", Token);

        var expected = new JsonObject
        {
            ["enabled"] = true,
            ["folder"] = Path.Combine(factory.DataFolder, "recordings"),
            ["keepDays"] = 7,
            ["maxDiskBytes"] = 20L * 1024 * 1024 * 1024,
            ["writeBufferBytes"] = 1024 * 1024,
            ["writeIntervalMs"] = 1000,
            ["compress"] = true,
            ["compressionLevel"] = 3,
        };
        Assert.True(JsonNode.DeepEquals(expected, json!["values"]), json!["values"]!.ToJsonString());
        Assert.True(JsonNode.DeepEquals(expected, json["defaults"]));
        Assert.True(File.Exists(Path.Combine(factory.DataFolder, "marketgrade.db")));
    }

    [Fact]
    public async Task Valid_settings_are_saved_and_survive_a_restart()
    {
        var dataFolder = Path.Combine(Path.GetTempPath(), "marketgrade-tests", Guid.NewGuid().ToString("N"));
        var settings = ValidSettings(dataFolder);
        settings["enabled"] = false;
        settings["keepDays"] = 30;
        settings["writeIntervalMs"] = 250;
        settings["compressionLevel"] = 9;

        await using (var factory = new BackendFactory { DataFolder = dataFolder })
        {
            var response = await factory.CreateAuthorizedClient().PutAsJsonAsync("/settings/recording", settings, Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(JsonNode.DeepEquals(settings, await response.Content.ReadFromJsonAsync<JsonObject>(Token)));
        }

        await using var restarted = new BackendFactory { DataFolder = dataFolder };
        var json = await restarted.CreateAuthorizedClient().GetFromJsonAsync<JsonObject>("/settings/recording", Token);
        Assert.True(JsonNode.DeepEquals(settings, json!["values"]), json["values"]!.ToJsonString());
        Assert.True(Directory.Exists(settings["folder"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Invalid_settings_return_an_error_for_each_field()
    {
        await using var factory = new BackendFactory();
        var notWritable = Path.Combine(factory.DataFolder, "a-file");
        Directory.CreateDirectory(factory.DataFolder);
        File.WriteAllText(notWritable, "");
        var settings = new JsonObject
        {
            ["enabled"] = true,
            ["folder"] = notWritable,
            ["keepDays"] = 0,
            ["maxDiskBytes"] = 1024,
            ["writeBufferBytes"] = 1024,
            ["writeIntervalMs"] = 50,
            ["compress"] = true,
            ["compressionLevel"] = 20,
        };

        var response = await factory.CreateAuthorizedClient().PutAsJsonAsync("/settings/recording", settings, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        var errors = json.RootElement.GetProperty("errors");
        Assert.Equal(
            ["compressionLevel", "folder", "keepDays", "maxDiskBytes", "writeBufferBytes", "writeIntervalMs"],
            errors.EnumerateObject().Select(e => e.Name).Order());
        Assert.StartsWith("Cannot create this folder", errors.GetProperty("folder")[0].GetString());
        Assert.Equal("Must be between 1 and 19.", errors.GetProperty("compressionLevel")[0].GetString());

        // Nothing was saved.
        var current = await factory.CreateAuthorizedClient().GetFromJsonAsync<JsonObject>("/settings/recording", Token);
        Assert.Equal(7, current!["values"]!["keepDays"]!.GetValue<int>());
    }

    [Fact]
    public async Task Maximum_disk_space_cannot_exceed_the_free_space()
    {
        await using var factory = new BackendFactory();
        var settings = ValidSettings(factory.DataFolder);
        settings["maxDiskBytes"] = long.MaxValue / 2;

        var response = await factory.CreateAuthorizedClient().PutAsJsonAsync("/settings/recording", settings, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token));
        Assert.Contains("the free space plus the current recordings", json.RootElement.GetProperty("errors").GetProperty("maxDiskBytes")[0].GetString());
    }

    private static JsonObject ValidSettings(string dataFolder) => new()
    {
        ["enabled"] = true,
        ["folder"] = Path.Combine(dataFolder, "elsewhere"),
        ["keepDays"] = 7,
        ["maxDiskBytes"] = 2L * 1024 * 1024 * 1024,
        ["writeBufferBytes"] = 256 * 1024,
        ["writeIntervalMs"] = 1000,
        ["compress"] = true,
        ["compressionLevel"] = 3,
    };
}
