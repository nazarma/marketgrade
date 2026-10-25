namespace Marketgrade.Engine.Recording;

/// <summary>The user's recording settings. A change is applied by swapping the whole record.</summary>
public sealed record RecordingSettings
{
    public const long GB = 1024L * 1024 * 1024;

    public bool Enabled { get; init; } = true;

    public required string Folder { get; init; }

    public int KeepDays { get; init; } = 7;

    public long MaxDiskBytes { get; init; } = 20 * GB;

    public int WriteBufferBytes { get; init; } = 1024 * 1024;

    public TimeSpan WriteInterval { get; init; } = TimeSpan.FromSeconds(1);

    public bool Compress { get; init; } = true;

    public int CompressionLevel { get; init; } = 3;

    public static RecordingSettings Default(string folder) => new() { Folder = folder };
}
