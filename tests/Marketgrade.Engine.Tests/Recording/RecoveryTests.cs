using Marketgrade.Engine.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine.Tests.Recording;

public class RecoveryTests
{
    private static readonly long s_hour = RecordingFiles.HourOf(new DateTimeOffset(2026, 3, 29, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Half_written_last_record_is_cut_off()
    {
        using var tempFolder = new TempFolder();
        var folder = tempFolder.Path;
        var path = RecordingFiles.PathFor(folder, "Test", s_hour);
        TestRecordings.Write(path, [TestRecordings.Message(1, "first"), TestRecordings.Message(2, "second")]);
        var validLength = new FileInfo(path).Length;
        // A crash in the middle of the third record: its header and half of its payload made it to disk.
        var third = TestRecordings.Build([TestRecordings.Message(3, "third message")]);
        var headerLength = RecordFormat.FileHeaderSize("Test");
        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.Write(third.AsSpan(headerLength, RecordFormat.RecordHeaderSize + 5));
        }

        var (files, cut) = RecordingRecovery.RecoverExchange(RecordingFiles.ExchangeFolder(folder, "Test"), NullLogger.Instance);

        Assert.Equal(1, files);
        Assert.Equal(RecordFormat.RecordHeaderSize + 5, cut);
        Assert.Equal(validLength, new FileInfo(path).Length);
        Assert.Equal([1, 2], TestRecordings.Read(path).Select(r => r.Seq));
    }

    [Fact]
    public void Corrupted_record_and_everything_after_it_is_cut_off()
    {
        using var tempFolder = new TempFolder();
        var folder = tempFolder.Path;
        var path = RecordingFiles.PathFor(folder, "Test", s_hour);
        var bytes = TestRecordings.Build([TestRecordings.Message(1, "first"), TestRecordings.Message(2, "second"), TestRecordings.Message(3, "third")]);
        var secondPayload = bytes.Length - 2 * RecordFormat.RecordHeaderSize - "second".Length - "third".Length;
        bytes[secondPayload] ^= 0xFF;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);

        RecordingRecovery.RecoverFile(path, NullLogger.Instance);

        Assert.Equal([1], TestRecordings.Read(path).Select(r => r.Seq));
    }

    [Fact]
    public void Intact_files_and_compressed_files_are_left_unchanged()
    {
        using var tempFolder = new TempFolder();
        var folder = tempFolder.Path;
        var path = RecordingFiles.PathFor(folder, "Test", s_hour);
        var compressed = RecordingFiles.PathFor(folder, "Test", s_hour - 1, compressed: true);
        TestRecordings.Write(path, [TestRecordings.Message(1, "first")]);
        File.WriteAllBytes(compressed, [1, 2, 3]);
        var before = File.ReadAllBytes(path);

        var (files, cut) = RecordingRecovery.RecoverExchange(RecordingFiles.ExchangeFolder(folder, "Test"), NullLogger.Instance);

        Assert.Equal((1, 0), (files, cut));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(compressed));
    }

    [Fact]
    public void File_without_a_complete_header_is_deleted_and_leftover_temp_files_are_removed()
    {
        using var tempFolder = new TempFolder();
        var folder = tempFolder.Path;
        var empty = RecordingFiles.PathFor(folder, "Test", s_hour);
        var temp = RecordingFiles.PathFor(folder, "Test", s_hour - 1, compressed: true) + ".tmp";
        var foreign = Path.Combine(Path.GetDirectoryName(empty)!, "notes.mgrec.zst.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(empty)!);
        File.WriteAllBytes(empty, "MGREC"u8.ToArray());
        File.WriteAllBytes(temp, [1, 2, 3]);
        File.WriteAllBytes(foreign, [1, 2, 3]);

        RecordingRecovery.RecoverExchange(RecordingFiles.ExchangeFolder(folder, "Test"), NullLogger.Instance);

        Assert.False(File.Exists(empty));
        Assert.False(File.Exists(temp));
        Assert.True(File.Exists(foreign));
    }
}
