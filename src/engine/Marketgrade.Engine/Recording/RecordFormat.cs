using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Marketgrade.Engine.Recording;

public readonly record struct FileHeader(ushort Version, int ExchangeId, string ExchangeName, long CreatedUnixNs);

public readonly record struct RecordHeader(RecordType Type, int PayloadLength, uint Crc, long Seq, long ArrivalUnixNs, long ConnectionId);

/// <summary>Version 1 of the recording file format, described in docs/recording-format.md. All numbers are little-endian.</summary>
public static class RecordFormat
{
    public const ushort Version = 1;

    public const int RecordHeaderSize = 40;

    public const int GapPayloadSize = 24;

    /// <summary>Larger payload lengths can only come from a broken record.</summary>
    public const int MaxPayloadLength = 256 * 1024 * 1024;

    public static ReadOnlySpan<byte> Magic => "MGREC\0"u8;

    /// <summary>Magic, version, exchange id and the name's length byte; the name and the created time follow.</summary>
    private const int FileHeaderFixedSize = 6 + 2 + 2 + 1;

    public static int FileHeaderSize(string exchangeName) => FileHeaderFixedSize + Encoding.UTF8.GetByteCount(exchangeName) + 8;

    public static int WriteFileHeader(Span<byte> destination, int exchangeId, string exchangeName, long createdUnixNs)
    {
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], checked((ushort)exchangeId));
        var nameLength = Encoding.UTF8.GetBytes(exchangeName, destination[FileHeaderFixedSize..]);
        destination[10] = checked((byte)nameLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[(FileHeaderFixedSize + nameLength)..], createdUnixNs);
        return FileHeaderFixedSize + nameLength + 8;
    }

    /// <summary>Reads a file header from the start of <paramref name="source"/>.</summary>
    /// <returns>The header length, 0 if <paramref name="source"/> is too short, or -1 if it is not a version 1 recording.</returns>
    public static int TryReadFileHeader(ReadOnlySpan<byte> source, out FileHeader header)
    {
        header = default;
        if (source.Length < FileHeaderFixedSize)
        {
            return source.StartsWith(Magic[..Math.Min(source.Length, Magic.Length)]) ? 0 : -1;
        }
        if (!source.StartsWith(Magic) || BinaryPrimitives.ReadUInt16LittleEndian(source[6..]) != Version)
        {
            return -1;
        }
        var nameLength = source[10];
        var length = FileHeaderFixedSize + nameLength + 8;
        if (source.Length < length)
        {
            return 0;
        }
        header = new FileHeader(
            Version,
            BinaryPrimitives.ReadUInt16LittleEndian(source[8..]),
            Encoding.UTF8.GetString(source.Slice(FileHeaderFixedSize, nameLength)),
            BinaryPrimitives.ReadInt64LittleEndian(source[(FileHeaderFixedSize + nameLength)..]));
        return length;
    }

    /// <summary>Writes the 40-byte record header for <paramref name="payload"/>, including its CRC32.</summary>
    public static void WriteRecordHeader(
        Span<byte> destination, RecordType type, ReadOnlySpan<byte> payload, long seq, long arrivalUnixNs, long connectionId)
    {
        destination[..RecordHeaderSize].Clear();
        destination[0] = (byte)type;
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], (uint)payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], Crc32.HashToUInt32(payload));
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], seq);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], arrivalUnixNs);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..], connectionId);
    }

    /// <summary>Reads a record header; false if its type or payload length cannot be valid.</summary>
    public static bool TryReadRecordHeader(ReadOnlySpan<byte> source, out RecordHeader header)
    {
        var type = (RecordType)source[0];
        var length = BinaryPrimitives.ReadUInt32LittleEndian(source[4..]);
        header = new RecordHeader(
            type,
            (int)Math.Min(length, int.MaxValue),
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[32..]));
        return type is >= RecordType.Message and <= RecordType.Gap && length <= MaxPayloadLength;
    }

    public static void WriteGapPayload(Span<byte> destination, long firstSeq, long lastSeq, long count)
    {
        BinaryPrimitives.WriteInt64LittleEndian(destination, firstSeq);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], lastSeq);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], count);
    }

    public static (long FirstSeq, long LastSeq, long Count) ReadGapPayload(ReadOnlySpan<byte> payload) => (
        BinaryPrimitives.ReadInt64LittleEndian(payload),
        BinaryPrimitives.ReadInt64LittleEndian(payload[8..]),
        BinaryPrimitives.ReadInt64LittleEndian(payload[16..]));

    public static bool IsValid(in RecordHeader header, ReadOnlySpan<byte> payload) => Crc32.HashToUInt32(payload) == header.Crc;
}
