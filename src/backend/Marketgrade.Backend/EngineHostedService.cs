using Marketgrade.Engine;

namespace Marketgrade.Backend;

/// <summary>
/// Starts the exchange connections with the backend and stops them on shutdown, after which every
/// message buffer is back in the pool. MARKETGRADE_ENGINE_DISABLED=1 keeps the engine stopped (tests).
/// </summary>
public sealed class EngineHostedService(
    MarketEngine engine,
    IConfiguration configuration,
    ILogger<EngineHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration["MARKETGRADE_ENGINE_DISABLED"] == "1")
        {
            logger.LogInformation("Engine is disabled, not connecting to exchanges");
            return Task.CompletedTask;
        }

        engine.Start();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await engine.StopAsync();
        logger.LogInformation("Engine stopped, {Available} of {Created} buffers in the pool", engine.Pool.Available, engine.Pool.Created);
    }
}
