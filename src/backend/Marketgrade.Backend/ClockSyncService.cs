using Marketgrade.Engine;

namespace Marketgrade.Backend;

/// <summary>Runs <see cref="ClockSync"/> every second for the engine. MARKETGRADE_ENGINE_DISABLED=1 turns it off (tests).</summary>
public sealed class ClockSyncService(
    MarketEngine engine,
    IConfiguration configuration,
    ILogger<ClockSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration["MARKETGRADE_ENGINE_DISABLED"] == "1")
        {
            return;
        }
        var sync = new ClockSync(sample => engine.SyncClock(sample.NtpUnixNs, sample.Timestamp), logger);
        try
        {
            while (true)
            {
                await sync.TickAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
