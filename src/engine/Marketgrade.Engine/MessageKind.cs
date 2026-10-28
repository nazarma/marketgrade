namespace Marketgrade.Engine;

public enum MessageKind
{
    /// <summary>Order book data.</summary>
    Data,

    /// <summary>Public trades.</summary>
    Trade,

    Heartbeat,
    Control,
    Error,
}
