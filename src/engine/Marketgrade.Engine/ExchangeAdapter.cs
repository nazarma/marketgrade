namespace Marketgrade.Engine;

/// <summary>Everything that differs between exchanges. Connections, queues and consumers are shared code.</summary>
public abstract class ExchangeAdapter
{
    public abstract int Id { get; }

    public abstract string Name { get; }

    public abstract string Pair { get; }

    public abstract Uri Uri { get; }

    /// <summary>UTF-8 messages sent after every connect, in order.</summary>
    public abstract IReadOnlyList<byte[]> SubscribeMessages { get; }

    /// <summary>How long the exchange keeps a connection open before closing it on schedule, if it does.</summary>
    public virtual TimeSpan? MaxConnectionLifetime => null;

    /// <summary>Classifies one complete message. Must not allocate.</summary>
    public abstract MessageKind Classify(ReadOnlySpan<byte> message);
}
