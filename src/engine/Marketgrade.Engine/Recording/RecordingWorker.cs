using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Marketgrade.Engine.Recording;

/// <summary>
/// The background work of recording, on one task and never on a recorder: compresses finished hours
/// and, every retention interval, compresses hours missed so far.
/// </summary>
internal sealed class RecordingWorker(RecordingManager manager)
{
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
            while (manager.CompressionQueue.Reader.TryRead(out var path))
            {
                if (manager.Settings.Compress)
                {
                    await CompressAsync(path, stopToken);
                }
            }
            if (_lastSweep == 0 || manager.Time.GetElapsedTime(_lastSweep) >= manager.Options.RetentionInterval)
            {
                _lastSweep = manager.Time.GetTimestamp();
                await CompressMissedHoursAsync(stopToken);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            manager.Logger.LogWarning("Recording maintenance failed: {Error}", e.Message);
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
