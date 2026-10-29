using Marketgrade.Engine.Books;
using Marketgrade.Engine.History;

namespace Marketgrade.Backend.Tests;

internal static class HistoryTestData
{
    /// <summary>A full minute of one exchange; <paramref name="delayMs"/> fills the coarse histogram with that many 1-delay samples.</summary>
    public static MinuteStats Minute(
        int exchangeId,
        DateTime minuteUtc,
        double valid = 60,
        double invalid = 0,
        double running = 60,
        int reconnects = 0,
        int gaps = 0,
        double delayMs = 30,
        long delays = 100,
        double? maxMs = null,
        long negative = 0,
        double? aheadMs = null)
    {
        var coarse = new long[CoarseHistogram.Buckets];
        if (delays > 0)
        {
            coarse[CoarseHistogram.IndexOf((long)(delayMs * 1e6))] = delays;
        }
        return new MinuteStats(exchangeId, exchangeId switch { 1 => "Kraken", 2 => "Coinbase", _ => "Binance" }, minuteUtc,
            running, running, valid, invalid, 1_000, 20, 300_000, delays,
            delays > 0 ? delayMs : null, delays > 0 ? delayMs * 2 : null, delays > 0 ? maxMs ?? delayMs * 3 : null, coarse,
            7_000, 40_000, null, null, 0, reconnects, gaps, 81_176.77m, 81_176.78m, 0.01m,
            negative, aheadMs);
    }
}
