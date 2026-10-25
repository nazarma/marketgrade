using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine.Tests.Recording;

public class RetentionTests
{
    private static readonly DateTimeOffset s_now = new(2026, 3, 29, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Recordings_older_than_the_retention_period_are_deleted()
    {
        var harness = new RecorderHarness(s => s with { KeepDays = 2 }, s_now);
        var tooOld = CreateFile(harness, s_now.AddDays(-2).AddHours(-1), compressed: true);
        var kept = CreateFile(harness, s_now.AddDays(-2).AddHours(1), compressed: true);
        var other = Path.Combine(Path.GetDirectoryName(tooOld)!, "notes.txt");
        File.WriteAllText(other, "not a recording");
        var worker = new RecordingWorker(harness.Manager);

        worker.DeleteOldRecordings();

        Assert.False(File.Exists(tooOld));
        Assert.True(File.Exists(kept));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void Oldest_recordings_are_deleted_until_the_total_fits()
    {
        var harness = new RecorderHarness(s => s with { MaxDiskBytes = 2_500 }, s_now);
        var files = Enumerable.Range(1, 4).Select(i => CreateFile(harness, s_now.AddHours(-i), compressed: true)).ToList();
        var worker = new RecordingWorker(harness.Manager);

        worker.DeleteOldRecordings();

        // Each file is 1,000 bytes: the two oldest go.
        Assert.Equal([true, true, false, false], files.Select(File.Exists));
    }

    [Fact]
    public void Retention_never_deletes_the_active_file_or_anything_outside_the_folder_layout()
    {
        var harness = new RecorderHarness(s => s with { KeepDays = 1, MaxDiskBytes = 0 }, s_now);
        var old = CreateFile(harness, s_now.AddDays(-5), compressed: false);
        var oldDateFolder = Path.GetDirectoryName(old)!;
        var currentHour = CreateFile(harness, s_now, compressed: false, exchange: "another");
        harness.Write(harness.Buffer(1, "active", s_now));
        var root = harness.RecordingsFolder;
        string[] foreign =
        [
            Path.Combine(root, "readme.txt"),
            Path.Combine(root, "test", "01.mgrec"),
            Path.Combine(root, "test", "backup", "01.mgrec"),
            Path.Combine(root, "Other", "2026-03-20", "01.mgrec.zst"),
            Path.Combine(root, "test", "2026-03-20", "01.mgrec.bak"),
        ];
        foreach (var path in foreign)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[1_000]);
        }
        var worker = new RecordingWorker(harness.Manager);

        worker.DeleteOldRecordings();

        Assert.False(File.Exists(old));
        Assert.False(Directory.Exists(oldDateFolder));
        Assert.True(File.Exists(harness.Recorder.ActiveFile));
        Assert.True(File.Exists(currentHour));
        Assert.All(foreign, path => Assert.True(File.Exists(path), path));
        harness.Close();
    }

    private static string CreateFile(RecorderHarness harness, DateTimeOffset hour, bool compressed, string exchange = "Test")
    {
        var path = RecordingFiles.PathFor(harness.RecordingsFolder, exchange, RecordingFiles.HourOf(hour), compressed: compressed);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[1_000]);
        return path;
    }
}
