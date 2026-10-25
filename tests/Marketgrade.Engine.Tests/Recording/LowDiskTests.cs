using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine.Tests.Recording;

public class LowDiskTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 3, 29, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Recording_pauses_on_low_disk_and_resumes_with_a_gap_record()
    {
        using var harness = new RecorderHarness(now: s_t0);
        long free = 10 * RecordingSettings.GB;
        var worker = new RecordingWorker(harness.Manager, _ => free);
        harness.Write(harness.Buffer(1, "before", s_t0));

        free = RecordingSettings.GB / 2;
        worker.CheckDisk();
        harness.Write(harness.Buffer(2, "paused", s_t0), harness.Buffer(3, "paused", s_t0));

        Assert.Equal(RecordingState.PausedLowDisk, harness.Recorder.State);
        Assert.Null(harness.Recorder.ActiveFile);
        Assert.Contains(harness.Events.GetRecent(10), e => e.Event == "Recording paused, disk almost full");

        // Between the two limits the pause holds.
        free = RecordingSettings.GB * 3 / 2;
        worker.CheckDisk();
        harness.Write(harness.Buffer(4, "still paused", s_t0));
        Assert.Equal(RecordingState.PausedLowDisk, harness.Recorder.State);

        free = 3 * RecordingSettings.GB;
        worker.CheckDisk();
        harness.Write(harness.Buffer(5, "resumed", s_t0));
        harness.Close();

        Assert.Equal(RecordingState.Recording, harness.Recorder.State);
        Assert.Contains(harness.Events.GetRecent(10), e => e.Event == "Recording resumed");
        var records = TestRecordings.Read(harness.PathFor(s_t0));
        Assert.Equal([RecordType.Message, RecordType.Gap, RecordType.Message], records.Select(r => r.Type));
        Assert.Equal((2, 4, 3), RecordFormat.ReadGapPayload(records[1].Payload));
        Assert.Equal(5, records[2].Seq);
        Assert.Equal(harness.Pool.Created, harness.Pool.Available);
    }

    [Fact]
    public void Disk_check_reports_total_and_today_sizes()
    {
        using var harness = new RecorderHarness(now: s_t0);
        var yesterday = harness.PathFor(s_t0.AddDays(-1), compressed: true);
        var today = harness.PathFor(s_t0.AddHours(-2), compressed: true);
        foreach (var (path, size) in new[] { (yesterday, 300), (today, 200) })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[size]);
        }
        var worker = new RecordingWorker(harness.Manager, _ => 42 * RecordingSettings.GB);

        worker.CheckDisk();

        Assert.Equal(new DiskUsage(500, 200, 42 * RecordingSettings.GB), harness.Manager.DiskUsage);
    }
}
