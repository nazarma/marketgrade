namespace Marketgrade.Engine.Exchanges;

public static class ExchangeAdapters
{
    public static IReadOnlyList<ExchangeAdapter> CreateDefault() =>
        [new KrakenAdapter(), new CoinbaseAdapter(), new BinanceAdapter()];
}
