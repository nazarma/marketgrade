using Marketgrade.Engine;
using Microsoft.AspNetCore.SignalR;

namespace Marketgrade.Backend;

/// <summary>Pushes the engine status once per second and every new event as it happens to all clients of the live hub.</summary>
public sealed class LivePushService(
    MarketEngine engine,
    IHubContext<LiveHub> hub,
    BackendMetrics metrics,
    ILogger<LivePushService> logger) : BackgroundService
{
    public static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(PushStatusAsync(stoppingToken), PushEventsAsync(stoppingToken));

    private async Task PushStatusAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(StatusInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SendAsync("status", engine.Status, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PushEventsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var connectionEvent in engine.NewEvents.ReadAllAsync(stoppingToken))
            {
                await SendAsync("event", connectionEvent, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task SendAsync(string method, object message, CancellationToken stoppingToken)
    {
        try
        {
            await hub.Clients.All.SendAsync(method, message, stoppingToken);
            metrics.HubMessagesSent.Add(1, new KeyValuePair<string, object?>("type", method));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning("Could not push {Method}: {Error}", method, e.Message);
        }
    }
}
