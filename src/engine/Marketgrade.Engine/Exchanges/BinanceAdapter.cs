using System.Text.Json;

namespace Marketgrade.Engine.Exchanges;

/// <summary>
/// Binance spot diff depth stream. The stream is named in the URL, so nothing is sent after connecting.
/// Ping frames are answered by <see cref="System.Net.WebSockets.ClientWebSocket"/> itself.
/// </summary>
public sealed class BinanceAdapter(Uri? uri = null) : ExchangeAdapter
{
    // The market-data-only host: same public streams as stream.binance.com, on port 443.
    public static readonly Uri DefaultUri = new("wss://data-stream.binance.vision/ws/btcusdt@depth@100ms");

    public override int Id => 3;

    public override string Name => "Binance";

    public override string Pair => "BTCUSDT";

    public override Uri Uri { get; } = uri ?? DefaultUri;

    public override IReadOnlyList<byte[]> SubscribeMessages { get; } = [];

    public override TimeSpan? MaxConnectionLifetime => TimeSpan.FromHours(24);

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
                if (reader.ValueTextEquals("e"u8))
                {
                    reader.Read();
                    return reader.ValueTextEquals("depthUpdate"u8) ? MessageKind.Data : MessageKind.Control;
                }
                if (reader.ValueTextEquals("code"u8) || reader.ValueTextEquals("error"u8))
                {
                    return MessageKind.Error;
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
