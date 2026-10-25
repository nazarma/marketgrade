using ZstdSharp;

namespace Marketgrade.Engine.Recording;

/// <summary>
/// Reads a recording file record by record and checks every CRC. Reading stops at the end of the
/// file or at the first broken record; <see cref="Error"/> then says which.
/// </summary>
public sealed class RecordingReader : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _header = new byte[RecordFormat.RecordHeaderSize];
    private byte[] _payload = new byte[64 * 1024];

    public RecordingReader(Stream stream)
    {
        _stream = stream;
        // The longest possible file header: 255 name bytes.
        var start = new byte[11 + 255 + 8];
        var read = stream.ReadAtLeast(start.AsSpan(0, 11), 11, throwOnEndOfStream: false);
        if (read == 11 && start.AsSpan(0, 6).SequenceEqual(RecordFormat.Magic))
        {
            var rest = start[10] + 8;
            read += stream.ReadAtLeast(start.AsSpan(11, rest), rest, throwOnEndOfStream: false);
        }
        var length = RecordFormat.TryReadFileHeader(start.AsSpan(0, read), out var header);
        if (length <= 0)
        {
            Error = length == 0 ? "Incomplete file header" : "Not a recording file";
            return;
        }
        Header = header;
        ValidLength = length;
    }

    /// <summary>Opens a <c>.mgrec</c> or <c>.mgrec.zst</c> file.</summary>
    public static RecordingReader Open(string path)
    {
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        if (path.EndsWith(".zst", StringComparison.Ordinal))
        {
            stream = new DecompressionStream(stream);
        }
        return new RecordingReader(stream);
    }

    public FileHeader Header { get; }

    /// <summary>Uncompressed bytes up to the end of the last valid record.</summary>
    public long ValidLength { get; private set; }

    /// <summary>Why reading stopped before the end of the file, or null.</summary>
    public string? Error { get; private set; }

    public RecordHeader Current { get; private set; }

    /// <summary>The current record's payload, valid until the next call to <see cref="Read"/>.</summary>
    public ReadOnlySpan<byte> Payload => _payload.AsSpan(0, Current.PayloadLength);

    /// <summary>Moves to the next valid record; false at the end of the file or at a broken record.</summary>
    public bool Read()
    {
        if (Error is not null)
        {
            return false;
        }

        var read = _stream.ReadAtLeast(_header, _header.Length, throwOnEndOfStream: false);
        if (read == 0)
        {
            return false;
        }
        if (read < _header.Length)
        {
            return Fail("Incomplete record header");
        }
        if (!RecordFormat.TryReadRecordHeader(_header, out var header))
        {
            return Fail("Invalid record header");
        }

        if (_payload.Length < header.PayloadLength)
        {
            _payload = new byte[Math.Max(header.PayloadLength, _payload.Length * 2)];
        }
        if (_stream.ReadAtLeast(_payload.AsSpan(0, header.PayloadLength), header.PayloadLength, throwOnEndOfStream: false) < header.PayloadLength)
        {
            return Fail("Incomplete record payload");
        }
        if (!RecordFormat.IsValid(header, _payload.AsSpan(0, header.PayloadLength)))
        {
            return Fail("CRC mismatch");
        }
        if (header.Type == RecordType.Gap && header.PayloadLength != RecordFormat.GapPayloadSize)
        {
            return Fail("Invalid gap payload");
        }

        Current = header;
        ValidLength += RecordFormat.RecordHeaderSize + header.PayloadLength;
        return true;
    }

    public void Dispose() => _stream.Dispose();

    private bool Fail(string error)
    {
        Error = $"{error} at byte {ValidLength}";
        return false;
    }
}
