using System.Text.Json;
using Marketgrade.Engine;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task Flow_goes_only_to_subscribers_and_stops_when_they_unsubscribe()
    {
        var engine = factory.Services.GetRequiredService<MarketEngine>();
        var subscriptions = factory.Services.GetRequiredService<FlowSubscriptions>();
        await using var watcher = LiveHubClient.Create(factory);
        await using var other = LiveHubClient.Create(factory);
        var watcherFrames = 0;
        var otherFrames = 0;
        var firstFrame = new TaskCompletionSource<JsonElement>();
        watcher.On<JsonElement>("flow", frame =>
        {
            Interlocked.Increment(ref watcherFrames);
            firstFrame.TrySetResult(frame);
        });
        other.On<JsonElement>("flow", _ => Interlocked.Increment(ref otherFrames));
        await watcher.StartAsync(TestContext.Current.CancellationToken);
        await other.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(engine.FlowEnabled);

        await watcher.InvokeAsync("SubscribeFlow", TestContext.Current.CancellationToken);
        var frame = await firstFrame.Task.WaitAsync(s_timeout, TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.True(engine.FlowEnabled);
        Assert.Equal(["Kraken", "Coinbase", "Binance"], frame.GetProperty("exchanges").EnumerateArray().Select(e => e.GetProperty("exchange").GetString()));
        Assert.InRange(Volatile.Read(ref watcherFrames), 3, 10);
        Assert.Equal(0, Volatile.Read(ref otherFrames));

        await watcher.InvokeAsync("UnsubscribeFlow", TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => !engine.FlowEnabled);
        var afterUnsubscribe = Volatile.Read(ref watcherFrames);
        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.InRange(Volatile.Read(ref watcherFrames) - afterUnsubscribe, 0, 1);
        Assert.Equal(0, subscriptions.Count);
    }

    [Fact]
    public async Task A_closed_connection_stops_its_flow()
    {
        var engine = factory.Services.GetRequiredService<MarketEngine>();
        var connection = LiveHubClient.Create(factory);
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await connection.InvokeAsync("SubscribeFlow", TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => engine.FlowEnabled);

        await connection.DisposeAsync();

        await WaitUntilAsync(() => !engine.FlowEnabled);
        Assert.Equal(0, factory.Services.GetRequiredService<FlowSubscriptions>().Count);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Wrong_token_cannot_connect()
    {
        await using var connection = LiveHubClient.Create(factory, token: "wrong");

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync(TestContext.Current.CancellationToken));
    }
}
