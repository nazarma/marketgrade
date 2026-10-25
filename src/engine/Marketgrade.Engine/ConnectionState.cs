namespace Marketgrade.Engine;

public enum ConnectionState
{
    Connecting,
    Live,
    Reconnecting,
    Blocked,
    Stopped,
}
