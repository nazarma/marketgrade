using System.Text;
using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine.Tests.Recording;

public class RecorderTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 3, 29, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Every_record_reads_back_identical_with_valid_crcs()
    {
        // A small write buffer, so the large message goes straight to the file.
        var harness = new RecorderHarness(s => s with { WriteBufferBytes = 64 * 1024 });
        var large = new string('x', 100_000);

        harness.Write(
            harness.Buffer(0, "ws://127.0.0.1/ws", s_t0, RecordType.ConnectionOpened),
            harness.Buffer(1, "{\"a\":1}", s_t0.AddMilliseconds(1)),
            harness.Buffer(2, large, s_t0.AddMilliseconds(2)),
            harness.Buffer(3, "{\"b\":2}", s_t0.AddMilliseconds(3)),
            harness.Buffer(3, "Closed by server", s_t0.AddMilliseconds(4), RecordType.ConnectionClosed));
        harness.Close();

        using var reader = RecordingReader.Open(harness.PathFor(s_t0));
        Assert.Equal(new FileHeader(1, 7, "Test", RecorderHarness.UnixNs(s_t0)), reader.Header);
        var records = TestRecordings.Read(reader);
        Assert.Equal(
            [RecordType.ConnectionOpened, RecordType.Message, RecordType.Message, RecordType.Message, RecordType.ConnectionClosed],
            records.Select(r => r.Type));
        Assert.Equal([0, 1, 2, 3, 3], records.Select(r => r.Seq));
        Assert.Equal(Enumerable.Range(0, 5).Select(i => RecorderHarness.UnixNs(s_t0.AddMilliseconds(i))), records.Select(r => r.ArrivalUnixNs));
        Assert.Equal(large, Encoding.UTF8.GetString(records[2].Payload));
        Assert.Equal("Closed by server", Encoding.UTF8.GetString(records[4].Payload));
        Assert.Equal(harness.Pool.Created, harness.Pool.Available);
    }

    [Fact]
    public void Records_go_to_the_file_of_their_arrival_hour()
    {
        var beforeMidnight = new DateTimeOffset(2026, 3, 29, 23, 59, 59, 900, TimeSpan.Zero);
        var afterMidnight = beforeMidnight.AddMilliseconds(200);
        var harness = new RecorderHarness(now: afterMidnight.AddSeconds(5));

        harness.Write(
            harness.Buffer(1, "late in hour 23", beforeMidnight),
            harness.Buffer(2, "early in hour 0", afterMidnight),
            // The clock moved back: the record stays in the current file instead of reopening hour 23.
            harness.Buffer(3, "clock moved back", beforeMidnight));
        harness.Close();

        var hour23 = harness.PathFor(beforeMidnight);
        var hour0 = harness.PathFor(afterMidnight);
        Assert.EndsWith(Path.Combine("test", "2026-03-29", "23.mgrec"), hour23);
        Assert.EndsWith(Path.Combine("test", "2026-03-30", "00.mgrec"), hour0);
        Assert.Equal([1], TestRecordings.Read(hour23).Select(r => r.Seq));
        Assert.Equal([2, 3], TestRecordings.Read(hour0).Select(r => r.Seq));
        // Only the finished hour is queued for compression; hour 0 is still the current hour.
        Assert.Equal([hour23], harness.QueuedForCompression());
    }

    [Fact]
    public void Reopening_an_hour_appends_to_its_file()
    {
        var harness = new RecorderHarness();
        harness.Write(harness.Buffer(1, "first", s_t0));
        harness.Close();

        var second = new Recorder(harness.Adapter, harness.Manager);
        second.BeginBatch();
        second.Process(harness.Buffer(2, "second", s_t0.AddMinutes(1)));
        second.CloseFile();

        Assert.Equal([1, 2], TestRecordings.Read(harness.PathFor(s_t0)).Select(r => r.Seq));
    }

    [Fact]
    public void An_hour_that_is_already_compressed_continues_in_a_second_part()
    {
        var harness = new RecorderHarness();
        var compressed = harness.PathFor(s_t0, compressed: true);
        Directory.CreateDirectory(Path.GetDirectoryName(compressed)!);
        File.WriteAllBytes(compressed, [1]);

        harness.Write(harness.Buffer(1, "after compression", s_t0));
        harness.Close();

        Assert.False(File.Exists(harness.PathFor(s_t0)));
        Assert.Equal([1], TestRecordings.Read(harness.PathFor(s_t0, part: 2)).Select(r => r.Seq));
    }

    [Fact]
    public void Gap_stamped_by_the_connection_is_written_before_its_message()
    {
        var harness = new RecorderHarness();
        var message = harness.Buffer(10, "after the gap", s_t0);
        message.GapFirstSeq = 5;
        message.GapLastSeq = 9;
        message.GapCount = 5;

        harness.Write(harness.Buffer(4, "before the gap", s_t0), message);
        harness.Close();

        var records = TestRecordings.Read(harness.PathFor(s_t0));
        Assert.Equal([RecordType.Message, RecordType.Gap, RecordType.Message], records.Select(r => r.Type));
        Assert.Equal((5, 9, 5), RecordFormat.ReadGapPayload(records[1].Payload));
        Assert.Equal(4, records[1].Seq);
    }

    [Fact]
    public async Task Quiet_period_still_flushes_on_the_write_interval()
    {
        var harness = new RecorderHarness(s => s with { WriteInterval = TimeSpan.FromSeconds(2) });
        harness.Start();
        harness.Send(harness.Buffer(1, "only message", s_t0));
        await RecorderHarness.WaitUntilAsync(() => harness.Recorder.ActiveFile is not null);
        Assert.Equal(0, new FileInfo(harness.PathFor(s_t0)).Length);

        harness.Time.Advance(TimeSpan.FromSeconds(2));

        await RecorderHarness.WaitUntilAsync(() => new FileInfo(harness.PathFor(s_t0)).Length > 0);
        Assert.Equal([1], TestRecordings.Read(harness.PathFor(s_t0)).Select(r => r.Seq));
        await harness.StopAsync();
        Assert.Null(harness.Recorder.ActiveFile);
    }
}
