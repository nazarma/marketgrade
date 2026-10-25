using Marketgrade.Engine.Recording;

namespace Marketgrade.Backend;

/// <summary>Checks recording settings before they are saved. Errors are keyed by the API's field names.</summary>
public static class RecordingSettingsValidator
{
    private const long KB = 1024;
    private const long MB = 1024 * KB;

    /// <summary>Validates <paramref name="settings"/>; a valid folder is created and returned in its full form.</summary>
    public static Dictionary<string, string[]> Validate(RecordingSettingsDto settings, out string? folder)
    {
        var errors = new Dictionary<string, string[]>();
        void Check(bool valid, string field, string message)
        {
            if (!valid)
            {
                errors[field] = [message];
            }
        }

        folder = CheckFolder(settings.Folder, out var folderError);
        Check(folderError is null, "folder", folderError!);
        Check(settings.KeepDays is >= 1 and <= 3650, "keepDays", "Must be between 1 and 3,650 days.");
        Check(settings.WriteBufferBytes is >= (int)(64 * KB) and <= (int)(64 * MB), "writeBufferBytes", "Must be between 64 KB and 64 MB.");
        Check(settings.WriteIntervalMs is >= 100 and <= 60_000, "writeIntervalMs", "Must be between 100 ms and 60 s.");
        Check(settings.CompressionLevel is >= 1 and <= 19, "compressionLevel", "Must be between 1 and 19.");

        if (settings.MaxDiskBytes < RecordingSettings.GB)
        {
            Check(false, "maxDiskBytes", "Must be at least 1 GB.");
        }
        else if (folder is not null && RecordingFiles.FreeSpace(folder) is { } free)
        {
            // Recordings already in the folder count as available: retention can delete them.
            var limit = free + RecordingFiles.TotalBytes(folder);
            Check(settings.MaxDiskBytes <= limit, "maxDiskBytes",
                $"Must be at most {Math.Floor(limit / (double)RecordingSettings.GB):0} GB, the free space plus the current recordings.");
        }
        return errors;
    }

    private static string? CheckFolder(string? folder, out string? error)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            error = "Choose a folder.";
            return null;
        }
        if (!Path.IsPathFullyQualified(folder))
        {
            error = "Must be a full path.";
            return null;
        }

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        try
        {
            Directory.CreateDirectory(fullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"Cannot create this folder: {e.Message}";
            return null;
        }
        try
        {
            var probe = Path.Combine(fullPath, $".marketgrade-write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = "This folder is not writable.";
            return null;
        }
        error = null;
        return fullPath;
    }
}
