using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine.Tests.Recording;

public class CompressionTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 3, 29, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Compressed_file_reads_back_identical_and_replaces_the_original()
    {
        using var harness = new RecorderHarness(now: s_t0);
        var path = harness.PathFor(s_t0.AddHours(-1));
        TestRecord[] records = [.. Enumerable.Range(1, 1_000).Select(i => TestRecordings.Message(i, $"{{\"price\":{60_000 + i % 50},\"size\":0.{i}}}"))];
        TestRecordings.Write(path, records);
        var worker = new RecordingWorker(harness.Manager);

        Assert.True(await worker.CompressAsync(path, TestContext.Current.CancellationToken));

        var compressed = harness.PathFor(s_t0.AddHours(-1), compressed: true);
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(compressed + ".tmp"));
        Assert.Equal(records, TestRecordings.Read(compressed), new RecordComparer());
        Assert.True(new FileInfo(compressed).Length < TestRecordings.Build(records).Length / 4);
        var compressedEvent = Assert.Single(harness.Events.GetRecent(10));
        Assert.Equal("Recording compressed", compressedEvent.Event);
        Assert.Matches(@"^09:00 to 10:00 UTC file, [\d.,]+ KB to [\d.,]+ (KB|B)$", compressedEvent.Detail);
    }

    [Fact]
    public async Task Damaged_file_stays_uncompressed_and_leaves_no_temp_file()
    {
        using var harness = new RecorderHarness(now: s_t0);
        var path = harness.PathFor(s_t0.AddHours(-1));
        TestRecordings.Write(path, [TestRecordings.Message(1, "first"), TestRecordings.Message(2, "second")]);
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        var worker = new RecordingWorker(harness.Manager);

        Assert.False(await worker.CompressAsync(path, TestContext.Current.CancellationToken));

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        Assert.Equal("Recording not compressed", Assert.Single(harness.Events.GetRecent(10)).Event);
    }

    [Fact]
    public async Task Queued_and_missed_hours_are_compressed_but_not_the_active_file()
    {
        using var harness = new RecorderHarness(now: s_t0);
        var queued = harness.PathFor(s_t0.AddHours(-1));
        var missed = harness.PathFor(s_t0.AddHours(-5));
        TestRecordings.Write(queued, [TestRecordings.Message(1, "queued")]);
        TestRecordings.Write(missed, [TestRecordings.Message(1, "missed")]);
        harness.Write(harness.Buffer(1, "active", s_t0));
        harness.Manager.QueueCompression(queued);
        harness.Manager.QueueCompression(harness.Recorder.ActiveFile!);
        var worker = new RecordingWorker(harness.Manager);

        await worker.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(queued + ".zst"));
        Assert.True(File.Exists(missed + ".zst"));
        Assert.True(File.Exists(harness.PathFor(s_t0)));
        Assert.False(File.Exists(harness.PathFor(s_t0, compressed: true)));
        harness.Close();
    }

    private sealed class RecordComparer : IEqualityComparer<TestRecord>
    {
        public bool Equals(TestRecord? x, TestRecord? y) =>
            x! with { Payload = [] } == y! with { Payload = [] } && x.Payload.AsSpan().SequenceEqual(y.Payload);

        public int GetHashCode(TestRecord obj) => obj.Seq.GetHashCode();
    }
}
