namespace Marketgrade.Engine.Recording;

public enum RecordType : byte
{
    /// <summary>Payload: the raw message bytes.</summary>
    Message = 1,

    /// <summary>Payload: the UTF-8 URL.</summary>
    ConnectionOpened = 2,

    /// <summary>Payload: the UTF-8 reason.</summary>
    ConnectionClosed = 3,

    /// <summary>Payload: first seq, last seq and count of the messages not recorded (three i64).</summary>
    Gap = 4,
}
