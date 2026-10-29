using System.Net.Sockets;

namespace Marketgrade.Backend;

/// <summary>
/// Keeps the engine's NTP time: a sync sends <see cref="Requests"/> requests to a time server and keeps the
/// answer with the shortest round trip, whose error is the smallest. It syncs at start and every 10 minutes,
/// and at once when the wall clock and the monotonic clock part by more than 20 ms (macOS stepped or slewed
/// the time, or the Mac slept). When no server answers it tries again after a minute, and the last sync stays.
/// </summary>
public sealed class ClockSync(
    Action<SntpSample> apply,
    ILogger logger,
    TimeProvider? time = null,
    Func<string, CancellationToken, Task<SntpSample>>? query = null,
    TimeSpan? spacing = null)
{
    public const int Requests = 8;

    public static readonly TimeSpan WarnAbove = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxDivergence = TimeSpan.FromMilliseconds(20);

    private static readonly string[] s_servers = ["time.apple.com", "pool.ntp.org"];
    private static readonly TimeSpan s_retry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<string, CancellationToken, Task<SntpSample>> _query =
        query ?? ((server, token) => SntpClient.QueryAsync(server, SntpClient.Port, s_timeout, token));
    private readonly TimeSpan _spacing = spacing ?? TimeSpan.FromMilliseconds(100);

    private bool _synced;
    private long? _dueTimestamp;

    // The wall clock and the monotonic clock at the last sync, to see them part.
    private long _wallUnixNs;
    private long _timestamp;

    /// <summary>Called every second: syncs when one is due or the clocks have parted.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_dueTimestamp is not { } due || _time.GetTimestamp() >= due)
        {
            await SyncAsync(cancellationToken);
        }
        else if (_synced && Divergence() is var divergence && divergence.Duration() > MaxDivergence)
        {
            logger.LogWarning("Clock corrected by {Correction:0.0} ms", divergence.TotalMilliseconds);
            await SyncAsync(cancellationToken);
        }
    }

    /// <summary>The answer with the shortest round trip of the first server that answers at all, or null.</summary>
    public async Task<SntpSample?> MeasureAsync(CancellationToken cancellationToken)
    {
        foreach (var server in s_servers)
        {
            SntpSample? best = null;
            string? error = null;
            for (var i = 0; i < Requests; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(_spacing, cancellationToken);
                }
                try
                {
                    var sample = await _query(server, cancellationToken);
                    if (best is null || sample.RoundTrip < best.Value.RoundTrip)
                    {
                        best = sample;
                    }
                }
                catch (Exception e) when (e is SocketException or TimeoutException or InvalidDataException)
                {
                    error = e.Message;
                }
            }
            if (best is { } found)
            {
                var offset = found.Offset;
                if (offset.Duration() > WarnAbove)
                {
                    logger.LogWarning("Clock offset {Offset:0.0} ms from {Server} (round trip {RoundTrip:0.0} ms): this clock is more than {Limit} ms off",
                        offset.TotalMilliseconds, server, found.RoundTrip.TotalMilliseconds, WarnAbove.TotalMilliseconds);
                }
                else
                {
                    logger.LogInformation("Clock offset {Offset:0.0} ms from {Server} (round trip {RoundTrip:0.0} ms)",
                        offset.TotalMilliseconds, server, found.RoundTrip.TotalMilliseconds);
                }
                return found;
            }
            logger.LogInformation("{Server} did not give the time: {Error}", server, error);
        }
        return null;
    }

    private async Task SyncAsync(CancellationToken cancellationToken)
    {
        var sample = await MeasureAsync(cancellationToken);
        if (sample is { } found)
        {
            apply(found);
            _synced = true;
            (_wallUnixNs, _timestamp) = (found.WallUnixNs, found.Timestamp);
        }
        else
        {
            logger.LogWarning(_synced
                ? "No time server answered; delays stay on the last sync"
                : "No time server answered; delays are shown without clock correction");
            // Parted clocks are measured from now on, so a failed sync is not tried again every second.
            (_wallUnixNs, _timestamp) = (WallUnixNs(), _time.GetTimestamp());
        }
        _dueTimestamp = _time.GetTimestamp() + (long)((sample is null ? s_retry : Interval).TotalSeconds * _time.TimestampFrequency);
    }

    /// <summary>How far the wall clock moved beyond the monotonic clock since the last sync.</summary>
    private TimeSpan Divergence() =>
        TimeSpan.FromTicks((WallUnixNs() - _wallUnixNs) / 100) - _time.GetElapsedTime(_timestamp);

    private long WallUnixNs() => (_time.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
}
