using System.Net.Sockets;
using Marketgrade.Engine;

namespace Marketgrade.Backend;

/// <summary>
/// Measures the local clock against NTP at start and every 10 minutes and passes the offset to the engine,
/// which corrects every delay with it. When no server answers the offset is unknown and delays are shown
/// uncorrected; the next attempt is then after a minute. MARKETGRADE_ENGINE_DISABLED=1 turns it off (tests).
/// </summary>
public sealed class ClockOffsetService(
    MarketEngine engine,
    IConfiguration configuration,
    ILogger<ClockOffsetService> logger) : BackgroundService
{
    public static readonly TimeSpan WarnAbove = TimeSpan.FromMilliseconds(50);

    private static readonly string[] s_servers = ["time.apple.com", "pool.ntp.org"];
    private static readonly TimeSpan s_interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_retry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration["MARKETGRADE_ENGINE_DISABLED"] == "1")
        {
            return;
        }
        try
        {
            while (true)
            {
                var offset = await MeasureAsync(stoppingToken);
                engine.SetClockOffset(offset);
                await Task.Delay(offset is null ? s_retry : s_interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task<TimeSpan?> MeasureAsync(CancellationToken cancellationToken)
    {
        foreach (var server in s_servers)
        {
            try
            {
                var offset = await SntpClient.QueryOffsetAsync(server, SntpClient.Port, s_timeout, cancellationToken);
                if (offset.Duration() > WarnAbove)
                {
                    logger.LogWarning("Clock offset {Offset:0.0} ms from {Server}: this clock is more than {Limit} ms off",
                        offset.TotalMilliseconds, server, WarnAbove.TotalMilliseconds);
                }
                else
                {
                    logger.LogInformation("Clock offset {Offset:0.0} ms from {Server}", offset.TotalMilliseconds, server);
                }
                return offset;
            }
            catch (Exception e) when (e is SocketException or TimeoutException or InvalidDataException)
            {
                logger.LogInformation("{Server} did not give the time: {Error}", server, e.Message);
            }
        }
        logger.LogWarning("No time server answered; delays are shown without clock correction");
        return null;
    }
}
