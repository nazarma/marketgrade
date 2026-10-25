using Marketgrade.Engine.Recording;

namespace Marketgrade.Backend;

/// <summary>Recording settings as the API and the settings database see them: sizes in bytes, times in milliseconds.</summary>
public sealed record RecordingSettingsDto(
    bool Enabled,
    string? Folder,
    int KeepDays,
    long MaxDiskBytes,
    int WriteBufferBytes,
    int WriteIntervalMs,
    bool Compress,
    int CompressionLevel)
{
    public static RecordingSettingsDto From(RecordingSettings settings) => new(
        settings.Enabled,
        settings.Folder,
        settings.KeepDays,
        settings.MaxDiskBytes,
        settings.WriteBufferBytes,
        (int)settings.WriteInterval.TotalMilliseconds,
        settings.Compress,
        settings.CompressionLevel);

    public RecordingSettings ToSettings() => new()
    {
        Enabled = Enabled,
        Folder = Folder!,
        KeepDays = KeepDays,
        MaxDiskBytes = MaxDiskBytes,
        WriteBufferBytes = WriteBufferBytes,
        WriteInterval = TimeSpan.FromMilliseconds(WriteIntervalMs),
        Compress = Compress,
        CompressionLevel = CompressionLevel,
    };
}
