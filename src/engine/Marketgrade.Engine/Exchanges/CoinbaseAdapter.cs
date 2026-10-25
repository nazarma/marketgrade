using System.Text.Json;
using Marketgrade.Engine.Books;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Exchanges;

/// <summary>Coinbase Advanced Trade market data. Public channels need no authentication.</summary>
public sealed class CoinbaseAdapter(Uri? uri = null) : ExchangeAdapter
{
    public static readonly Uri DefaultUri = new("wss://advanced-trade-ws.coinbase.com");

    public override int Id => 2;

    public override string Name => "Coinbase";

    public override string Pair => "BTC-USD";

    public override Uri Uri { get; } = uri ?? DefaultUri;

    // One channel per subscribe message. Without the heartbeats channel Coinbase closes quiet connections.
    public override IReadOnlyList<byte[]> SubscribeMessages { get; } =
    [
        """{"type":"subscribe","product_ids":["BTC-USD"],"channel":"level2"}"""u8.ToArray(),
        """{"type":"subscribe","channel":"heartbeats"}"""u8.ToArray(),
    ];

    public override BookHandler CreateBookHandler(PairRules rules, EngineOptions options, ILogger? logger = null) =>
        new CoinbaseBookHandler(this, rules, options.CoinbaseBookCapacity, logger);

    public override MessageKind Classify(ReadOnlySpan<byte> message)
    {
        try
        {
            var reader = new Utf8JsonReader(message);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return MessageKind.Error;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("channel"u8))
                {
                    reader.Read();
                    if (reader.ValueTextEquals("l2_data"u8))
                    {
                        return MessageKind.Data;
                    }
                    return reader.ValueTextEquals("heartbeats"u8) ? MessageKind.Heartbeat : MessageKind.Control;
                }
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    if (reader.ValueTextEquals("error"u8))
                    {
                        return MessageKind.Error;
                    }
                    continue;
                }

                reader.Read();
                reader.Skip();
            }
            return MessageKind.Control;
        }
        catch (JsonException)
        {
            return MessageKind.Error;
        }
    }
}
