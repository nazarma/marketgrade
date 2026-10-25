using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine.Tests.Recording;

internal sealed record TestRecord(RecordType Type, long Seq, long ArrivalUnixNs, long ConnectionId, byte[] Payload);

/// <summary>Writes and reads recording files for tests.</summary>
internal static class TestRecordings
{
    public static byte[] Build(IEnumerable<TestRecord> records, int exchangeId = 7, string exchange = "Test", long createdUnixNs = 1)
    {
        using var output = new MemoryStream();
        var header = new byte[RecordFormat.FileHeaderSize(exchange)];
        RecordFormat.WriteFileHeader(header, exchangeId, exchange, createdUnixNs);
        output.Write(header);
        var recordHeader = new byte[RecordFormat.RecordHeaderSize];
        foreach (var record in records)
        {
            RecordFormat.WriteRecordHeader(recordHeader, record.Type, record.Payload, record.Seq, record.ArrivalUnixNs, record.ConnectionId);
            output.Write(recordHeader);
            output.Write(record.Payload);
        }
        return output.ToArray();
    }

    public static void Write(string path, IEnumerable<TestRecord> records, string exchange = "Test")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Build(records, exchange: exchange));
    }

    public static List<TestRecord> Read(string path) => Read(RecordingReader.Open(path));

    public static List<TestRecord> Read(RecordingReader reader)
    {
        using (reader)
        {
            var records = new List<TestRecord>();
            while (reader.Read())
            {
                var h = reader.Current;
                records.Add(new TestRecord(h.Type, h.Seq, h.ArrivalUnixNs, h.ConnectionId, reader.Payload.ToArray()));
            }
            Assert.Null(reader.Error);
            return records;
        }
    }

    public static TestRecord Message(long seq, string text, long arrivalUnixNs = 1_000, long connectionId = 1) =>
        new(RecordType.Message, seq, arrivalUnixNs, connectionId, System.Text.Encoding.UTF8.GetBytes(text));
}
