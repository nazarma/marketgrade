using System.Threading.Channels;
using Marketgrade.Engine.History;

namespace Marketgrade.Backend.History;

/// <summary>
/// The only writer of the history store, on its own task: never on an engine thread. Each exchange's worker
/// publishes its minute within a fraction of a second of the boundary, so the writer waits briefly after the
/// first one and inserts the minute's rows in one transaction. Gaps and reconnects go in as they arrive.
/// Once at start and then daily it deletes what is older than <see cref="KeepDays"/>.
/// </summary>
public sealed class HistoryWriter(HistoryFeed feed, HistoryStore store, ILogger<HistoryWriter> logger, TimeProvider? time = null) : BackgroundService
{
    public const int DefaultKeepDays = 90;

    private static readonly TimeSpan s_gather = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_retentionInterval = TimeSpan.FromDays(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private int _keepDays = DefaultKeepDays;

    /// <summary>History older than this many days is deleted; a change applies at once.</summary>
    public int KeepDays
    {
        get => Volatile.Read(ref _keepDays);
        set
        {
            Volatile.Write(ref _keepDays, value);
            DeleteOld();
        }
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(
            Run(WriteMinutesAsync, stoppingToken),
            Run(token => WriteEachAsync(feed.Gaps, store.Add, token), stoppingToken),
            Run(token => WriteEachAsync(feed.Reconnects, store.Add, token), stoppingToken),
            Run(RetainAsync, stoppingToken));

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        // The engine published its last minute when it stopped; the engine stops before this service.
        WriteQueued();
    }

    /// <summary>Deletes everything older than <see cref="KeepDays"/>.</summary>
    public void DeleteOld()
    {
        try
        {
            var deleted = store.DeleteBefore(_time.GetUtcNow().UtcDateTime.AddDays(-KeepDays));
            if (deleted > 0)
            {
                logger.LogInformation("Deleted {Minutes} minutes of history older than {Days} days", deleted, KeepDays);
            }
        }
        catch (Exception e) when (e is DuckDB.NET.Data.DuckDBException or InvalidOperationException)
        {
            logger.LogWarning("Could not delete old history: {Error}", e.Message);
        }
    }

    private async Task WriteMinutesAsync(CancellationToken stoppingToken)
    {
        var minutes = feed.Minutes;
        while (await minutes.WaitToReadAsync(stoppingToken))
        {
            await Task.Delay(s_gather, _time, stoppingToken);
            var batch = new List<MinuteStats>();
            while (minutes.TryRead(out var minute))
            {
                batch.Add(minute);
            }
            Write(() => store.Add(batch), "minutes");
        }
    }

    private async Task WriteEachAsync<T>(ChannelReader<T> reader, Action<T> add, CancellationToken stoppingToken)
    {
        await foreach (var item in reader.ReadAllAsync(stoppingToken))
        {
            Write(() => add(item), typeof(T).Name);
        }
    }

    private async Task RetainAsync(CancellationToken stoppingToken)
    {
        DeleteOld();
        using var timer = new PeriodicTimer(s_retentionInterval, _time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            DeleteOld();
        }
    }

    /// <summary>What the engine published while stopping.</summary>
    private void WriteQueued()
    {
        var batch = new List<MinuteStats>();
        while (feed.Minutes.TryRead(out var minute))
        {
            batch.Add(minute);
        }
        if (batch.Count > 0)
        {
            Write(() => store.Add(batch), "minutes");
        }
        while (feed.Gaps.TryRead(out var gap))
        {
            Write(() => store.Add(gap), "gap");
        }
        while (feed.Reconnects.TryRead(out var reconnect))
        {
            Write(() => store.Add(reconnect), "reconnect");
        }
    }

    private void Write(Action write, string what)
    {
        try
        {
            write();
        }
        catch (Exception e) when (e is DuckDB.NET.Data.DuckDBException or InvalidOperationException or IOException)
        {
            logger.LogWarning("Could not save {What} to the history: {Error}", what, e.Message);
        }
    }

    private static async Task Run(Func<CancellationToken, Task> loop, CancellationToken stoppingToken)
    {
        try
        {
            await loop(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
