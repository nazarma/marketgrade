using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Marketgrade.Engine.Recording;

/// <summary>
/// The background work of recording, on one task and never on a recorder: compresses finished hours,
/// checks the disk (pausing recording when it is almost full) and, every retention interval,
/// compresses hours missed so far and deletes old recordings.
/// </summary>
/// <param name="freeSpace">Free bytes on the drive of a folder, or null if unknown; tests replace it.</param>
internal sealed class RecordingWorker(RecordingManager manager, Func<string, long?>? freeSpace = null)
{
    private readonly Func<string, long?> _freeSpace = freeSpace ?? FreeSpace;

    /// <summary>Files that failed to compress are not tried again until the next start, when recovery has checked them.</summary>
    private readonly HashSet<string> _failed = [];
    private long _lastSweep;

    public async Task RunAsync(CancellationToken stopToken)
    {
        using var timer = new PeriodicTimer(manager.Options.RecordingCheckInterval, manager.Time);
        try
        {
            do
            {
                await RunOnceAsync(stopToken);
            }
            while (await timer.WaitForNextTickAsync(stopToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    internal async Task RunOnceAsync(CancellationToken stopToken)
    {
        try
        {
            CheckDisk();
            while (manager.CompressionQueue.Reader.TryRead(out var path))
            {
                if (manager.Settings.Compress && await CompressAsync(path, stopToken))
                {
                    DeleteOldRecordings();
                }
            }
            if (_lastSweep == 0 || manager.Time.GetElapsedTime(_lastSweep) >= manager.Options.RetentionInterval)
            {
                _lastSweep = manager.Time.GetTimestamp();
                await CompressMissedHoursAsync(stopToken);
                DeleteOldRecordings();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            manager.Logger.LogWarning("Recording maintenance failed: {Error}", e.Message);
        }
    }

    /// <summary>Updates the disk usage and pauses recording below the low-disk limit, resuming above the higher one.</summary>
    internal void CheckDisk()
    {
        var folder = manager.Settings.Folder;
        var files = RecordingFiles.Enumerate(folder);
        var today = RecordingFiles.HourOf(manager.Time.GetUtcNow()) / 24;
        var free = _freeSpace(folder);
        manager.DiskUsage = new DiskUsage(files.Sum(f => f.Length), files.Where(f => f.Hour / 24 == today).Sum(f => f.Length), free);
        if (free is not { } bytes)
        {
            return;
        }

        if (!manager.Paused && bytes < manager.Options.LowDiskPauseBytes)
        {
            manager.Paused = true;
            var detail = $"{Gigabytes(bytes)} free on the recordings drive, resuming above {Gigabytes(manager.Options.LowDiskResumeBytes)}";
            manager.Events.Add("Recording", "Recording paused, disk almost full", detail);
            manager.Logger.LogWarning("Recording paused: {Detail}", detail);
        }
        else if (manager.Paused && bytes > manager.Options.LowDiskResumeBytes)
        {
            manager.Paused = false;
            manager.Events.Add("Recording", "Recording resumed", $"{Gigabytes(bytes)} free on the recordings drive");
            manager.Logger.LogInformation("Recording resumed, {Free} free", Gigabytes(bytes));
        }
    }

    /// <summary>
    /// Deletes recordings older than the retention period, then the oldest until the total fits the
    /// maximum disk space. Only files in the folder layout are deleted, never the current hour's or one
    /// being written. Empty date folders are removed, except today's.
    /// </summary>
    internal void DeleteOldRecordings()
    {
        var settings = manager.Settings;
        var now = manager.Time.GetUtcNow();
        var currentHour = RecordingFiles.HourOf(now);
        var keepAfter = now.UtcDateTime - TimeSpan.FromDays(settings.KeepDays);
        var files = RecordingFiles.Enumerate(settings.Folder);
        var total = files.Sum(f => f.Length);
        long deletedFiles = 0, deletedBytes = 0;
        foreach (var file in files.Where(f => f.Hour < currentHour).OrderBy(f => f.Hour).ThenBy(f => f.Exchange).ThenBy(f => f.Part))
        {
            var tooOld = RecordingFiles.HourStart(file.Hour + 1) <= keepAfter;
            if (!tooOld && total <= settings.MaxDiskBytes)
            {
                break;
            }
            if (manager.IsActive(file.Path))
            {
                continue;
            }
            try
            {
                File.Delete(file.Path);
                total -= file.Length;
                deletedFiles++;
                deletedBytes += file.Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                manager.Logger.LogWarning("Could not delete {Path}: {Error}", file.Path, e.Message);
            }
        }

        foreach (var (folder, date) in RecordingFiles.DateFolders(settings.Folder))
        {
            if (date != now.UtcDateTime.Date && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        if (deletedFiles > 0)
        {
            manager.Logger.LogInformation("Deleted {Files} old recordings ({Bytes} bytes)", deletedFiles, deletedBytes);
        }
    }

    /// <summary>Compresses uncompressed files whose hour ended longer ago than the grace period.</summary>
    internal async Task CompressMissedHoursAsync(CancellationToken stopToken)
    {
        if (!manager.Settings.Compress)
        {
            return;
        }
        var finishedBefore = RecordingFiles.HourOf(manager.Time.GetUtcNow() - manager.Options.FinishedHourGrace);
        foreach (var file in RecordingFiles.Enumerate(manager.Settings.Folder))
        {
            if (!file.Compressed && file.Hour < finishedBefore)
            {
                await CompressAsync(file.Path, stopToken);
            }
        }
    }

    /// <summary>
    /// Compresses <c>HH.mgrec</c> to <c>HH.mgrec.zst.tmp</c>, decompresses it again and checks every
    /// record, then renames it to <c>HH.mgrec.zst</c> and deletes the original.
    /// </summary>
    internal async Task<bool> CompressAsync(string path, CancellationToken stopToken)
    {
        if (!RecordingFiles.TryParse(path, out var file) || file.Compressed
            || _failed.Contains(path) || manager.IsActive(path) || !File.Exists(path))
        {
            return false;
        }

        var target = path + ".zst";
        var temp = target + ".tmp";
        var started = Stopwatch.GetTimestamp();
        try
        {
            long sourceLength;
            await using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, useAsync: true))
            await using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            {
                await using (var zstd = new CompressionStream(output, manager.Settings.CompressionLevel, leaveOpen: true))
                {
                    await source.CopyToAsync(zstd, stopToken);
                }
                output.Flush(flushToDisk: true);
                sourceLength = source.Length;
            }

            var original = Scan(RecordingReader.Open(path));
            var compressed = Scan(new RecordingReader(new DecompressionStream(File.OpenRead(temp))));
            if (original.Error is not null)
            {
                throw new InvalidDataException($"the file is damaged ({original.Error})");
            }
            if (compressed.Error is not null || compressed.Records != original.Records || compressed.Length != sourceLength)
            {
                throw new InvalidDataException($"the compressed file does not match ({compressed.Error ?? "different records"})");
            }

            File.Move(temp, target);
            File.Delete(path);
            var exchange = manager.ExchangeName(file.Exchange);
            manager.Metrics?.RecordingFilesCompressed.Add(1, new KeyValuePair<string, object?>("exchange", exchange));
            manager.Logger.LogInformation("Compressed {Path}: {Before} to {After} bytes in {Ms:0} ms",
                path, sourceLength, new FileInfo(target).Length, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e)
        {
            _failed.Add(path);
            manager.Logger.LogWarning("Could not compress {Path}, keeping it uncompressed: {Error}", path, e.Message);
            return false;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    /// <summary>Free space on the drive of <paramref name="folder"/>, or of its nearest existing parent.</summary>
    private static long? FreeSpace(string folder)
    {
        try
        {
            var existing = new DirectoryInfo(folder);
            while (existing is { Exists: false })
            {
                existing = existing.Parent;
            }
            return existing is null ? null : new DriveInfo(existing.FullName).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string Gigabytes(long bytes) => $"{bytes / (double)RecordingSettings.GB:0.0} GB";

    private static (long Records, long Length, string? Error) Scan(RecordingReader reader)
    {
        using (reader)
        {
            long records = 0;
            while (reader.Read())
            {
                records++;
            }
            return (records, reader.ValidLength, reader.Error);
        }
    }
}
