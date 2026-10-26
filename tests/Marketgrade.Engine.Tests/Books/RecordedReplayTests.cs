using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Recording;

namespace Marketgrade.Engine.Tests.Books;

/// <summary>
/// The first minute of a real recording per exchange (2026-10-26, trimmed with <c>Marketgrade.Reader trim</c>),
/// rebuilt with the live handlers: every check passes, as it did live.
/// </summary>
public class RecordedReplayTests
{
    public static TheoryData<string> Exchanges() => ["kraken", "coinbase", "binance"];

    [Theory]
    [MemberData(nameof(Exchanges))]
    public void Recording_replays_without_a_failure(string exchange)
    {
        var (adapter, rules) = exchange switch
        {
            "kraken" => ((ExchangeAdapter)new KrakenAdapter(), new PairRules(1, "BTC/USD", 1, 8)),
            "coinbase" => (new CoinbaseAdapter(), new PairRules(2, "BTC-USD", 2, 8)),
            _ => (new BinanceAdapter(), new PairRules(3, "BTCUSDT", 2, 5)),
        };
        var replay = new BookReplay(adapter, rules);

        using (var reader = RecordingReader.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "replay", $"{exchange}.mgrec.zst")))
        {
            replay.Read(reader);
            Assert.Null(reader.Error);
        }
        replay.Finish();

        Assert.Empty(replay.Failures);
        Assert.Empty(replay.MissingSnapshots);
        Assert.Equal(1, replay.Syncs);
        Assert.Equal(BookState.Valid, replay.Book.State);
        Assert.True(replay.Applied > replay.Messages * 0.9, $"{replay.Applied} of {replay.Messages} applied");
        Assert.True(replay.Book.Bids.PriceAt(0) < replay.Book.Asks.PriceAt(0));
        Assert.InRange(replay.LastUnixNs - replay.FirstUnixNs, 59_000_000_000, 60_000_000_000);
    }
}
