using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Recording;

/// <summary>Repairs recordings after a crash: a crash can leave at most one partial record at the end of a file.</summary>
internal static class RecordingRecovery
{
    /// <summary>Repairs every uncompressed file of one exchange and deletes leftovers of interrupted compressions.</summary>
    /// <returns>The total number of bytes cut.</returns>
    public static long RecoverExchange(string exchangeFolder, ILogger logger)
    {
        if (!Directory.Exists(exchangeFolder))
        {
            return 0;
        }
        long cut = 0;
        foreach (var dateFolder in Directory.EnumerateDirectories(exchangeFolder))
        {
            foreach (var path in Directory.EnumerateFiles(dateFolder))
            {
                if (RecordingFiles.IsTempFile(path))
                {
                    File.Delete(path);
                    logger.LogInformation("Deleted {Path}, left over from an interrupted compression", path);
                }
                else if (RecordingFiles.TryParse(path, out var file) && !file.Compressed)
                {
                    cut += RecoverFile(path, logger);
                }
            }
        }
        return cut;
    }

    /// <summary>
    /// Truncates the file at its first broken record. A file without a complete file header holds no
    /// records and is deleted; a file that is not a recording is left alone.
    /// </summary>
    /// <returns>The number of bytes cut.</returns>
    public static long RecoverFile(string path, ILogger logger)
    {
        long validLength;
        bool hasHeader;
        string? error;
        using (var reader = RecordingReader.Open(path))
        {
            while (reader.Read())
            {
            }
            (validLength, hasHeader, error) = (reader.ValidLength, reader.HasHeader, reader.Error);
        }
        if (error is null)
        {
            return 0;
        }

        var length = new FileInfo(path).Length;
        if (!hasHeader)
        {
            if (error == RecordingReader.IncompleteFileHeader)
            {
                File.Delete(path);
                logger.LogWarning("Recording {Path} had no complete file header, deleted {Bytes} bytes", path, length);
                return length;
            }
            logger.LogWarning("{Path} is not a recording, left unchanged", path);
            return 0;
        }

        using (var file = File.OpenHandle(path, FileMode.Open, FileAccess.Write))
        {
            RandomAccess.SetLength(file, validLength);
        }
        logger.LogWarning("Recording {Path}: {Error}, truncated {Bytes} bytes", path, error, length - validLength);
        return length - validLength;
    }
}
