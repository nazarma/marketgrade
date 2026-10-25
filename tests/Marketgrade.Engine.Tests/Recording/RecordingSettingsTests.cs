using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine.Tests.Recording;

public class RecordingSettingsTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 3, 29, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void New_write_buffer_size_applies_to_the_next_batch()
    {
        var harness = new RecorderHarness(s => s with { WriteBufferBytes = 64 * 1024 });
        var large = new string('x', 100_000);
        harness.Write(harness.Buffer(1, large, s_t0));
        var path = harness.PathFor(s_t0);
        // Larger than the buffer: written to the file at once.
        Assert.True(new FileInfo(path).Length > 100_000);
        var lengthBefore = new FileInfo(path).Length;

        harness.Manager.Apply(harness.Manager.Settings with { WriteBufferBytes = 256 * 1024 });
        harness.Write(harness.Buffer(2, large, s_t0));

        // Fits the new buffer: nothing reaches the file until the next flush.
        Assert.Equal(lengthBefore, new FileInfo(path).Length);
        harness.Close();
        Assert.Equal([1, 2], TestRecordings.Read(path).Select(r => r.Seq));
    }

    [Fact]
    public void New_folder_gets_the_next_file_and_the_old_file_is_closed()
    {
        var harness = new RecorderHarness();
        harness.Write(harness.Buffer(1, "old folder", s_t0));
        var oldPath = harness.PathFor(s_t0);

        var newFolder = Path.Combine(harness.Folder, "elsewhere");
        harness.Manager.Apply(harness.Manager.Settings with { Folder = newFolder });
        harness.Write(harness.Buffer(2, "new folder", s_t0));

        var newPath = harness.PathFor(s_t0);
        Assert.StartsWith(newFolder, newPath);
        Assert.Equal(newPath, harness.Recorder.ActiveFile);
        Assert.Equal([1], TestRecordings.Read(oldPath).Select(r => r.Seq));
        harness.Close();
        Assert.Equal([2], TestRecordings.Read(newPath).Select(r => r.Seq));
    }

    [Fact]
    public void Turning_recording_off_closes_the_file_and_turning_it_on_resumes_with_a_gap()
    {
        var harness = new RecorderHarness();
        harness.Write(harness.Buffer(1, "on", s_t0));

        harness.Manager.Apply(harness.Manager.Settings with { Enabled = false });
        harness.Write(harness.Buffer(2, "off", s_t0));

        Assert.Equal(RecordingState.Off, harness.Recorder.State);
        Assert.Null(harness.Recorder.ActiveFile);
        Assert.Equal([1], TestRecordings.Read(harness.PathFor(s_t0)).Select(r => r.Seq));

        harness.Manager.Apply(harness.Manager.Settings with { Enabled = true });
        harness.Write(harness.Buffer(3, "on again", s_t0));
        harness.Close();

        var records = TestRecordings.Read(harness.PathFor(s_t0));
        Assert.Equal([RecordType.Message, RecordType.Gap, RecordType.Message], records.Select(r => r.Type));
        Assert.Equal((2, 2, 1), RecordFormat.ReadGapPayload(records[1].Payload));
        Assert.Equal(harness.Pool.Created, harness.Pool.Available);
    }

    [Fact]
    public async Task Running_engine_applies_new_settings_without_a_restart()
    {
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            for (var i = 0; !c.Closed.IsCompleted; i++)
            {
                await c.SendAsync($"{{\"n\":{i}}}");
                await Task.Delay(2);
            }
        });
        var folder = TestRecordings.CreateFolder();
        var settings = RecordingSettings.Default(Path.Combine(folder, "first")) with { WriteInterval = TimeSpan.FromMilliseconds(100) };
        await using var engine = new MarketEngine(
            new EngineOptions { ReconnectDelays = [TimeSpan.FromMilliseconds(50)], PoolSize = 64 },
            [new TestAdapter(server.Uri, "A")],
            recording: settings);
        engine.Start();
        await RecorderHarness.WaitUntilAsync(() => RecordingFiles.TotalBytes(settings.Folder) > 0);

        var second = settings with { Folder = Path.Combine(folder, "second"), WriteBufferBytes = 128 * 1024 };
        engine.ApplyRecordingSettings(second);
        await RecorderHarness.WaitUntilAsync(() => RecordingFiles.TotalBytes(second.Folder) > 0);
        await engine.StopAsync();

        var first = RecordingFiles.Enumerate(settings.Folder).SelectMany(f => TestRecordings.Read(f.Path)).ToList();
        var next = RecordingFiles.Enumerate(second.Folder).SelectMany(f => TestRecordings.Read(f.Path)).ToList();
        var seqs = first.Concat(next).Where(r => r.Type == RecordType.Message).Select(r => r.Seq).ToList();
        Assert.Equal(Enumerable.Range(1, seqs.Count).Select(i => (long)i), seqs);
        Assert.Equal(engine.Pool.Created, engine.Pool.Available);
    }
}
