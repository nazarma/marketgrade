using System.Net.Sockets;

namespace Marketgrade.Backend;

/// <summary>Where <see cref="ClockSync"/> puts its results: the engine, or a fake in tests.</summary>
public interface IClockTarget
{
    /// <summary>NTP time at a monotonic timestamp, and how much faster NTP time runs than the monotonic clock.</summary>
    void Sync(long ntpUnixNs, long timestamp, double rate);

    /// <summary>Skip delays until the next <see cref="Sync"/>.</summary>
    void Hold();

    /// <summary>Forget the last sync: delays are uncorrected until the next one.</summary>
    void Clear();
}

/// <summary>
/// Keeps the engine's NTP time. A sync sends <see cref="Requests"/> requests to a time server and keeps the
/// answer with the shortest round trip, whose error is the smallest; successive syncs give the monotonic
/// clock's rate error (<see cref="ClockRate"/>). It syncs at start and every 10 minutes, and checks every
/// second whether the computer slept or the wall clock moved more than 20 ms away from the monotonic clock
/// at its measured rate (macOS stepped or slewed the time, or the rate is not known yet). Either way delays
/// are held until a sync at once. The rate starts over after sleep and after a step, a jump of more than
/// 10 ms between two checks; a slow parting is a slew or the rate error, and keeps it. When no server
/// answers it tries again after a minute and the last sync stays, except after sleep; two failures in a row
/// also start the rate over.
/// </summary>
public sealed class ClockSync(
    IClockTarget target,
    ILogger logger,
    TimeProvider? time = null,
    Func<long?>? sleepClockNs = null,
    Func<string, CancellationToken, Task<SntpSample>>? query = null,
    TimeSpan? spacing = null)
{
    public const int Requests = 8;

    public static readonly TimeSpan WarnAbove = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxDivergence = TimeSpan.FromMilliseconds(20);

    /// <summary>The sleep-counting clock getting this far ahead of the monotonic clock between two checks is a wake from sleep.</summary>
    public static readonly TimeSpan MinSleep = TimeSpan.FromSeconds(1);

    /// <summary>The wall clock moving this far beyond the monotonic clock between two checks is a step.</summary>
    public static readonly TimeSpan MinStep = TimeSpan.FromMilliseconds(10);

    private static readonly string[] s_servers = ["time.apple.com", "pool.ntp.org"];
    private static readonly TimeSpan s_retry = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<long?> _sleepClockNs = sleepClockNs ?? SleepClock.Ns;
    private readonly Func<string, CancellationToken, Task<SntpSample>> _query =
        query ?? ((server, token) => SntpClient.QueryAsync(server, SntpClient.Port, s_timeout, token));
    private readonly TimeSpan _spacing = spacing ?? TimeSpan.FromMilliseconds(100);
    private ClockRate? _rate;
    private long? _dueTimestamp;
    private int _failures;

    // The sync the engine has, to take back after a hold when the sync after a step fails.
    private (long NtpUnixNs, long Timestamp, double Rate)? _applied;

    // The wall clock and the monotonic clock at the last sync, to see them part.
    private long _wallUnixNs;
    private long _timestamp;

    // The clocks at the last check, to see a wake from sleep or a step.
    private long _checkTimestamp;
    private long? _checkSleepNs;
    private long _checkWallUnixNs;
    private bool _checked;

    private ClockRate Rate => _rate ??= new ClockRate(_time.TimestampFrequency);

    /// <summary>Called every second: syncs when one is due, the computer slept or the clocks have parted.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var (woke, stepped) = Check();
        if (woke)
        {
            target.Hold();
            Rate.Reset();
            if (await SyncAsync(cancellationToken))
            {
                logger.LogInformation("Clock re-synced after sleep");
            }
            else if (_applied is not null)
            {
                // The monotonic clock stopped during sleep, so the last sync no longer holds.
                target.Clear();
                _applied = null;
            }
        }
        else if (_dueTimestamp is not { } due || _time.GetTimestamp() >= due)
        {
            await SyncAsync(cancellationToken);
        }
        else if (_applied is { } applied && Divergence(applied.Rate) is var divergence && divergence.Duration() > MaxDivergence)
        {
            logger.LogWarning("Clock corrected by {Correction:0.0} ms", divergence.TotalMilliseconds);
            target.Hold();
            if (stepped)
            {
                Rate.Reset();
            }
            if (!await SyncAsync(cancellationToken))
            {
                // A step of the wall clock leaves the last sync right on the monotonic clock.
                target.Sync(applied.NtpUnixNs, applied.Timestamp, applied.Rate);
            }
        }
    }

    /// <summary>The answer with the shortest round trip of the first server that answers at all, or null.</summary>
    public async Task<(SntpSample Sample, string Server)?> MeasureAsync(CancellationToken cancellationToken)
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
                return (found, server);
            }
            logger.LogInformation("{Server} did not give the time: {Error}", server, error);
        }
        return null;
    }

    private async Task<bool> SyncAsync(CancellationToken cancellationToken)
    {
        var measured = await MeasureAsync(cancellationToken);
        if (measured is var (sample, server))
        {
            Rate.Add(sample.NtpUnixNs, sample.Timestamp);
            target.Sync(sample.NtpUnixNs, sample.Timestamp, Rate.Rate);
            _applied = (sample.NtpUnixNs, sample.Timestamp, Rate.Rate);
            _failures = 0;
            (_wallUnixNs, _timestamp) = (sample.WallUnixNs, sample.Timestamp);
            var level = sample.Offset.Duration() > WarnAbove ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, "Clock offset {Offset:0.0} ms from {Server} (round trip {RoundTrip:0.0} ms, monotonic clock {Rate:+0.0;-0.0;0} ppm)",
                sample.Offset.TotalMilliseconds, server, sample.RoundTrip.TotalMilliseconds, Rate.Rate * 1e6);
        }
        else
        {
            if (++_failures >= 2)
            {
                Rate.Reset();
            }
            logger.LogWarning(_applied is null
                ? "No time server answered; delays are shown without clock correction"
                : "No time server answered; delays stay on the last sync");
            // Parted clocks are measured from now on, so a failed sync is not tried again every second.
            (_wallUnixNs, _timestamp) = (WallUnixNs(), _time.GetTimestamp());
        }
        _dueTimestamp = _time.GetTimestamp() + (long)((measured is null ? s_retry : Interval).TotalSeconds * _time.TimestampFrequency);
        return measured is not null;
    }

    /// <summary>
    /// Since the last check: whether the sleep-counting clock got ahead of the monotonic clock (the computer
    /// slept), and whether the wall clock jumped beyond it (a step).
    /// </summary>
    private (bool Woke, bool Stepped) Check()
    {
        var timestamp = _time.GetTimestamp();
        var sleepNs = _sleepClockNs();
        var wallUnixNs = WallUnixNs();
        var monotonicNs = _time.GetElapsedTime(_checkTimestamp, timestamp).TotalNanoseconds;
        var woke = _checked && sleepNs is { } now && _checkSleepNs is { } before && now - before - monotonicNs > MinSleep.TotalNanoseconds;
        var stepped = _checked && Math.Abs(wallUnixNs - _checkWallUnixNs - monotonicNs * (1 + (_applied?.Rate ?? 0))) > MinStep.TotalNanoseconds;
        (_checkTimestamp, _checkSleepNs, _checkWallUnixNs, _checked) = (timestamp, sleepNs, wallUnixNs, true);
        return (woke, stepped);
    }

    /// <summary>How far the wall clock moved beyond the monotonic clock (at the measured rate) since the last sync.</summary>
    private TimeSpan Divergence(double rate) =>
        TimeSpan.FromTicks((WallUnixNs() - _wallUnixNs) / 100) - _time.GetElapsedTime(_timestamp) * (1 + rate);

    private long WallUnixNs() => (_time.GetUtcNow().UtcTicks - DateTime.UnixEpoch.Ticks) * 100;
}
