using System.Diagnostics;

namespace Marketgrade.Backend;

/// <summary>
/// Stops the backend when the desktop shell that started it is gone,
/// so a crashed or force-killed shell never leaves the backend running.
/// </summary>
public sealed class ParentProcessWatcher(
    IConfiguration configuration,
    IHostApplicationLifetime lifetime,
    ILogger<ParentProcessWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan s_interval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!int.TryParse(configuration["MARKETGRADE_PARENT_PID"], out var parentPid))
        {
            return;
        }

        using var timer = new PeriodicTimer(s_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!IsRunning(parentPid))
            {
                logger.LogWarning("Parent process {ParentPid} has exited, shutting down", parentPid);
                lifetime.StopApplication();
                return;
            }
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
