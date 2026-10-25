using System.Text;

namespace Marketgrade.Engine.Tests;

internal sealed class TestAdapter(Uri uri, string name = "Test", params string[] subscribeMessages) : ExchangeAdapter
{
    public override int Id => 7;

    public override string Name => name;

    public override string Pair => "TST/USD";

    public override Uri Uri => uri;

    public override IReadOnlyList<byte[]> SubscribeMessages { get; } =
        [.. subscribeMessages.Select(Encoding.UTF8.GetBytes)];

    public TimeSpan? Lifetime { get; init; }

    public override TimeSpan? MaxConnectionLifetime => Lifetime;

    public override MessageKind Classify(ReadOnlySpan<byte> message) =>
        message.IndexOf("heartbeat"u8) >= 0 ? MessageKind.Heartbeat : MessageKind.Data;
}
