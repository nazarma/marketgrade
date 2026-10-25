using System.Text.Json;
using Marketgrade.Engine.Books;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Exchanges;

/// <summary>
/// Binance spot diff depth stream. The stream is named in the URL, so nothing is sent after connecting.
/// Ping frames are answered by <see cref="System.Net.WebSockets.ClientWebSocket"/> itself.
/// </summary>
public sealed class BinanceAdapter(Uri? uri = null, Uri? restUri = null) : ExchangeAdapter
{
    // The market-data-only host: same public streams as stream.binance.com, on port 443.
    public static readonly Uri DefaultUri = new("wss://data-stream.binance.vision/ws/btcusdt@depth@100ms");

    /// <summary>The market-data-only REST host, like the stream host.</summary>
    public static readonly Uri DefaultRestUri = new("https://data-api.binance.vision/");

    public override int Id => 3;

    public override string Name => "Binance";

    public override string Pair => "BTCUSDT";

    public override Uri Uri { get; } = uri ?? DefaultUri;

    public override IReadOnlyList<byte[]> SubscribeMessages { get; } = [];

    public override TimeSpan? MaxConnectionLifetime => TimeSpan.FromHours(24);

    public override Uri PairRulesUri { get; } = new(restUri ?? DefaultRestUri, "api/v3/exchangeInfo?symbol=BTCUSDT");

    /// <summary>The depth snapshot the book starts from: 5,000 levels per side, request weight 250.</summary>
    public Uri DepthSnapshotUri { get; } = new(restUri ?? DefaultRestUri, "api/v3/depth?symbol=BTCUSDT&limit=5000");

    /// <summary>Decimals of the <c>PRICE_FILTER</c> tick size and the <c>LOT_SIZE</c> step size.</summary>
    public override PairRules ParsePairRules(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var symbol = document.RootElement.GetProperty("symbols").EnumerateArray().Single(s => s.GetProperty("symbol").GetString() == Pair);
        string? tick = null, step = null;
        foreach (var filter in symbol.GetProperty("filters").EnumerateArray())
        {
            switch (filter.GetProperty("filterType").GetString())
            {
                case "PRICE_FILTER":
                    tick = filter.GetProperty("tickSize").GetString();
                    break;
                case "LOT_SIZE":
                    step = filter.GetProperty("stepSize").GetString();
                    break;
            }
        }
        if (tick is null || step is null)
        {
            throw new FormatException("Binance exchangeInfo has no PRICE_FILTER or LOT_SIZE");
        }
        return new PairRules(Id, Pair, PairRules.DecimalsFrom(tick), PairRules.DecimalsFrom(step));
    }

    public override BookHandler CreateBookHandler(PairRules rules, EngineOptions options, ILogger? logger = null) =>
        new BinanceBookHandler(this, rules, options.BinanceBookCapacity, logger);

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
