using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Marketgrade.Backend.History;
using Microsoft.Extensions.DependencyInjection;

namespace Marketgrade.Backend.Tests;

public class HistorySettingsEndpointTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Fresh_install_keeps_90_days()
    {
        await using var factory = new BackendFactory();

        var json = await factory.CreateAuthorizedClient().GetFromJsonAsync<JsonObject>("/settings/history", Token);

        Assert.Equal(90, json!["values"]!["keepDays"]!.GetValue<int>());
        Assert.Equal(90, json["defaults"]!["keepDays"]!.GetValue<int>());
        Assert.Equal(90, factory.Services.GetRequiredService<HistoryWriter>().KeepDays);
    }

    [Fact]
    public async Task A_valid_setting_is_applied_saved_and_survives_a_restart()
    {
        using var tempFolder = new TempFolder();

        await using (var factory = new BackendFactory { DataFolder = tempFolder.Path })
        {
            var response = await factory.CreateAuthorizedClient().PutAsJsonAsync("/settings/history", new { keepDays = 30 }, Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(30, (await response.Content.ReadFromJsonAsync<JsonObject>(Token))!["keepDays"]!.GetValue<int>());
            Assert.Equal(30, factory.Services.GetRequiredService<HistoryWriter>().KeepDays);
        }

        await using (var restarted = new BackendFactory { DataFolder = tempFolder.Path })
        {
            var json = await restarted.CreateAuthorizedClient().GetFromJsonAsync<JsonObject>("/settings/history", Token);
            Assert.Equal(30, json!["values"]!["keepDays"]!.GetValue<int>());
            Assert.Equal(30, restarted.Services.GetRequiredService<HistoryWriter>().KeepDays);
        }
    }

    [Theory]
    [InlineData(6)]
    [InlineData(3651)]
    public async Task Out_of_range_is_rejected_with_a_message_and_not_saved(int keepDays)
    {
        await using var factory = new BackendFactory();
        var client = factory.CreateAuthorizedClient();

        var response = await client.PutAsJsonAsync("/settings/history", new { keepDays }, Token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>(Token);
        Assert.Equal("Must be between 7 and 3,650 days.", problem!["errors"]!["keepDays"]![0]!.GetValue<string>());
        var json = await client.GetFromJsonAsync<JsonObject>("/settings/history", Token);
        Assert.Equal(90, json!["values"]!["keepDays"]!.GetValue<int>());
    }
}
