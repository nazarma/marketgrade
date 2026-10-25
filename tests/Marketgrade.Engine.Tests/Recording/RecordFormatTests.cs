using System.Text;
using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine.Tests.Recording;

public class RecordFormatTests
{
    [Fact]
    public void Every_record_type_reads_back_identical()
    {
        var gap = new byte[RecordFormat.GapPayloadSize];
        RecordFormat.WriteGapPayload(gap, 10, 19, 10);
        TestRecord[] records =
        [
            new(RecordType.ConnectionOpened, 0, 100, 1, Encoding.UTF8.GetBytes("wss://example.test/ws")),
            TestRecordings.Message(1, "{\"a\":1}", 200),
            new(RecordType.Gap, 9, 300, 1, gap),
            TestRecordings.Message(20, "", 400),
            TestRecordings.Message(21, new string('x', 200_000), 500),
            new(RecordType.BookReset, 21, 550, 1, Encoding.UTF8.GetBytes("Update ID gap: expected 101, got 105")),
            new(RecordType.Snapshot, 21, 560, 1, Encoding.UTF8.GetBytes("{\"lastUpdateId\":100,\"bids\":[],\"asks\":[]}")),
            new(RecordType.ConnectionClosed, 21, 600, 1, Encoding.UTF8.GetBytes("Closed by server")),
        ];

        using var reader = new RecordingReader(new MemoryStream(TestRecordings.Build(records, exchangeId: 3, exchange: "Coinbase", createdUnixNs: 42)));

        Assert.Equal(new FileHeader(1, 3, "Coinbase", 42), reader.Header);
        var read = TestRecordings.Read(reader);
        Assert.Equal(records.Length, read.Count);
        for (var i = 0; i < records.Length; i++)
        {
            Assert.Equal(records[i] with { Payload = [] }, read[i] with { Payload = [] });
            Assert.Equal(records[i].Payload, read[i].Payload);
        }
        Assert.Equal((10, 19, 10), RecordFormat.ReadGapPayload(read[2].Payload));
    }

    [Fact]
    public void Unknown_record_type_is_an_invalid_header()
    {
        var bytes = TestRecordings.Build([new(RecordType.BookReset, 1, 1, 1, [])]);
        var headerStart = bytes.Length - RecordFormat.RecordHeaderSize;
        bytes[headerStart] = 7;

        using var reader = new RecordingReader(new MemoryStream(bytes));

        Assert.False(reader.Read());
        Assert.StartsWith("Invalid record header", reader.Error);
    }

    [Fact]
    public void Corrupted_payload_stops_reading_at_the_broken_record()
    {
        var bytes = TestRecordings.Build([TestRecordings.Message(1, "first"), TestRecordings.Message(2, "second")]);
        var validLength = bytes.Length - RecordFormat.RecordHeaderSize - "second".Length;
        bytes[^1] ^= 0xFF;

        using var reader = new RecordingReader(new MemoryStream(bytes));

        Assert.True(reader.Read());
        Assert.False(reader.Read());
        Assert.Equal($"CRC mismatch at byte {validLength}", reader.Error);
        Assert.Equal(validLength, reader.ValidLength);
    }

    [Fact]
    public void Truncated_file_header_is_reported()
    {
        var bytes = TestRecordings.Build([]);

        using var reader = new RecordingReader(new MemoryStream(bytes[..^3]));

        Assert.Equal("Incomplete file header", reader.Error);
        Assert.False(reader.Read());
    }

    [Fact]
    public void Paths_follow_the_folder_layout()
    {
        var hour = RecordingFiles.HourOf(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero));

        var path = RecordingFiles.PathFor("/data", "Kraken", hour);

        Assert.Equal(Path.Combine("/data", "kraken", "2026-03-29", "01.mgrec"), path);
        Assert.True(RecordingFiles.TryParse(path, out var file));
        Assert.Equal(("kraken", hour, 1, false), (file.Exchange, file.Hour, file.Part, file.Compressed));
        Assert.True(RecordingFiles.TryParse(RecordingFiles.PathFor("/data", "Kraken", hour, part: 2, compressed: true), out var part));
        Assert.Equal((hour, 2, true), (part.Hour, part.Part, part.Compressed));
    }

    [Theory]
    [InlineData("kraken/2026-03-29/1.mgrec")]
    [InlineData("kraken/2026-03-29/24.mgrec")]
    [InlineData("kraken/2026-03-29/01.mgrec.bak")]
    [InlineData("kraken/2026-3-29/01.mgrec")]
    [InlineData("Kraken/2026-03-29/01.mgrec")]
    [InlineData("kraken/notes/01.mgrec")]
    [InlineData("kraken/2026-03-29/notes.txt")]
    public void Other_files_are_not_recordings(string relativePath)
    {
        Assert.False(RecordingFiles.TryParse(Path.Combine("/data", relativePath), out _));
    }
}
