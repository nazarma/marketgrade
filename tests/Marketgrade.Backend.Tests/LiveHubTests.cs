using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

namespace Marketgrade.Backend.Tests;

public class LiveHubTests(BackendFactory factory) : IClassFixture<BackendFactory>
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Status_is_pushed_to_every_client_once_per_second()
    {
        await using var connection = LiveHubClient.Create(factory);
        var statuses = new List<JsonElement>();
        var two = new TaskCompletionSource();
        connection.On<JsonElement>("status", status =>
        {
            lock (statuses)
            {
                statuses.Add(status);
                if (statuses.Count == 2)
                {
                    two.TrySetResult();
                }
            }
        });

        await connection.StartAsync(TestContext.Current.CancellationToken);
        await two.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);

        var exchanges = statuses[0].GetProperty("exchanges").EnumerateArray().ToList();
        Assert.Equal(["Kraken", "Coinbase", "Binance"], exchanges.Select(e => e.GetProperty("name").GetString()));
        Assert.Equal("Stopped", exchanges[0].GetProperty("state").GetString());
        Assert.Equal("Off", statuses[0].GetProperty("recording").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Wrong_token_cannot_connect()
    {
        await using var connection = LiveHubClient.Create(factory, token: "wrong");

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(TestContext.Current.CancellationToken));
    }
}
