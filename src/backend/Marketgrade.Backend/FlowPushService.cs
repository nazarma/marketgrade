using Marketgrade.Engine;
using Microsoft.AspNetCore.SignalR;

namespace Marketgrade.Backend;

/// <summary>
/// While at least one client watches the flow, turns on the engine's flow rings and pushes a sampled
/// <see cref="FlowFrame"/> to those clients every 100 ms. Without one it does nothing at all.
/// </summary>
public sealed class FlowPushService(
    MarketEngine engine,
    FlowSubscriptions subscriptions,
    IHubContext<LiveHub> hub,
    BackendMetrics metrics,
    ILogger<FlowPushService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                await subscriptions.WaitForSubscriberAsync(stoppingToken);
                engine.FlowEnabled = true;
                logger.LogInformation("Pushing the flow to {Count} client(s)", subscriptions.Count);
                try
                {
                    await PushAsync(stoppingToken);
                }
                finally
                {
                    engine.FlowEnabled = false;
                }
                logger.LogInformation("No client watches the flow, stopped pushing it");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PushAsync(CancellationToken stoppingToken)
    {
        var sampler = new FlowSampler(engine.Flows, DateTime.UtcNow);
        using var timer = new PeriodicTimer(Interval);
        while (subscriptions.Count > 0 && await timer.WaitForNextTickAsync(stoppingToken))
        {
            var frame = sampler.Sample(DateTime.UtcNow);
            try
            {
                await hub.Clients.Group(FlowSubscriptions.Group).SendAsync("flow", frame, stoppingToken);
                metrics.HubMessagesSent.Add(1, new KeyValuePair<string, object?>("type", "flow"));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning("Could not push the flow: {Error}", e.Message);
            }
        }
    }
}
