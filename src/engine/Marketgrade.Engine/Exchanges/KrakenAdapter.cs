using System.Text.Json;
using Marketgrade.Engine.Books;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Exchanges;

/// <summary>Kraken spot WebSocket v2. Heartbeats arrive automatically once any channel is subscribed.</summary>
public sealed class KrakenAdapter(Uri? uri = null) : ExchangeAdapter
{
    public static readonly Uri DefaultUri = new("wss://ws.kraken.com/v2");

    /// <summary>Levels per side we subscribe to and keep.</summary>
    public const int BookDepth = 1000;

    public override int Id => 1;

    public override string Name => "Kraken";

    public override string Pair => "BTC/USD";

    public override Uri Uri { get; } = uri ?? DefaultUri;

    // Kraken does not restore subscriptions after a reconnect; the connection sends these every time.
    public override IReadOnlyList<byte[]> SubscribeMessages { get; } =
    [
        """{"method":"subscribe","params":{"channel":"book","symbol":["BTC/USD"],"depth":1000}}"""u8.ToArray(),
    ];

    public override BookHandler CreateBookHandler(PairRules rules, EngineOptions options, ILogger? logger = null) =>
        new KrakenBookHandler(this, rules, options.KrakenBookCapacity, logger);

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
                    if (reader.ValueTextEquals("book"u8))
                    {
                        return MessageKind.Data;
                    }
                    return reader.ValueTextEquals("heartbeat"u8) ? MessageKind.Heartbeat : MessageKind.Control;
                }
                if (reader.ValueTextEquals("error"u8))
                {
                    return MessageKind.Error;
                }
                if (reader.ValueTextEquals("success"u8))
                {
                    reader.Read();
                    return reader.TokenType == JsonTokenType.False ? MessageKind.Error : MessageKind.Control;
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
