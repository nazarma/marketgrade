using Marketgrade.Engine.Books;
using Marketgrade.Engine.Flow;

namespace Marketgrade.Backend;

/// <summary>What the live hub pushes as <c>flow</c> every 100 ms to the clients that watch it.</summary>
/// <param name="IntervalSeconds">Time since the previous frame, for rates.</param>
public sealed record FlowFrame(DateTime TimeUtc, double IntervalSeconds, IReadOnlyList<ExchangeFlowFrame> Exchanges);

/// <summary>One exchange's part of a <see cref="FlowFrame"/>. Prices and amounts are plain numbers.</summary>
/// <param name="BookChanges">All book changes received in the interval, at any distance from the mid price.</param>
/// <param name="Trades">All trades received in the interval.</param>
/// <param name="Lost">Events overwritten in the ring before they could be sampled; they are still in the counts.</param>
/// <param name="Mid">Null while the book is not valid; likewise <paramref name="Bids"/> and <paramref name="Asks"/> are empty.</param>
/// <param name="Bids">Best first, <c>[price, amount]</c>, at most <see cref="FlowSampler.DepthLevels"/>.</param>
/// <param name="Events">The sample, oldest first: every trade up to <see cref="FlowSampler.MaxTrades"/> and the
/// <see cref="FlowSampler.MaxBookChanges"/> largest book changes by notional value.</param>
public sealed record ExchangeFlowFrame(
    int Id,
    string Exchange,
    string Pair,
    int PriceDecimals,
    long BookChanges,
    long Trades,
    long Lost,
    double? Mid,
    double[][] Bids,
    double[][] Asks,
    FlowItem[] Events);

/// <param name="TimeMs">Unix milliseconds: arrival for a book change, the exchange's time for a trade.</param>
/// <param name="Amount">For a book change the signed change at its level.</param>
public sealed record FlowItem(double TimeMs, FlowKind Kind, FlowSide Side, double Price, double Amount);

/// <summary>
/// Samples every exchange's flow ring since the previous call. Single-threaded; created when the first
/// client subscribes, so it starts with the events written from then on.
/// </summary>
public sealed class FlowSampler
{
    public const int MaxTrades = 50;

    public const int MaxBookChanges = 40;

    public const int DepthLevels = 12;

    private readonly Source[] _sources;
    private DateTime _lastUtc;

    public FlowSampler(IReadOnlyList<IExchangeFlow> flows, DateTime nowUtc)
    {
        _sources = [.. flows.Select(flow => new Source(flow))];
        _lastUtc = nowUtc;
    }

    public FlowFrame Sample(DateTime nowUtc)
    {
        var interval = (nowUtc - _lastUtc).TotalSeconds;
        _lastUtc = nowUtc;
        return new FlowFrame(nowUtc, interval, [.. _sources.Select(source => source.Sample())]);
    }

    private sealed class Source(IExchangeFlow flow)
    {
        private readonly FlowEvent[] _buffer = new FlowEvent[flow.Ring.Capacity];
        private readonly List<int> _trades = [];
        private readonly List<int> _changes = [];
        private long _next = flow.Ring.Published;
        private long _bookChanges = flow.BookChanges;
        private long _tradeCount = flow.Trades;

        public ExchangeFlowFrame Sample()
        {
            var read = flow.Ring.Read(_next, _buffer);
            _next = read.Next;
            var bookChanges = flow.BookChanges;
            var trades = flow.Trades;
            var rules = flow.Rules;
            var frame = new ExchangeFlowFrame(flow.ExchangeId, flow.Exchange, flow.Pair, rules?.PriceDecimals ?? 0,
                bookChanges - _bookChanges, trades - _tradeCount, read.Lost, null, [], [], []);
            _bookChanges = bookChanges;
            _tradeCount = trades;
            if (rules is null)
            {
                return frame;
            }

            _trades.Clear();
            _changes.Clear();
            for (var i = 0; i < read.Count; i++)
            {
                (_buffer[i].Kind == FlowKind.Trade ? _trades : _changes).Add(i);
            }
            if (_trades.Count > MaxTrades)
            {
                _trades.RemoveRange(0, _trades.Count - MaxTrades);
            }
            if (_changes.Count > MaxBookChanges)
            {
                // Largest notional first; the same price, so the largest change in amount.
                _changes.Sort((a, b) => Notional(_buffer[b]).CompareTo(Notional(_buffer[a])));
                _changes.RemoveRange(MaxBookChanges, _changes.Count - MaxBookChanges);
            }
            _trades.AddRange(_changes);
            _trades.Sort();

            var priceScale = Math.Pow(10, rules.PriceDecimals);
            var amountScale = Math.Pow(10, rules.AmountDecimals);
            var events = _trades.Select(i =>
            {
                var e = _buffer[i];
                return new FlowItem(e.TimeNs / 1e6, e.Kind, e.Side, e.Price / priceScale, e.Amount / amountScale);
            }).ToArray();

            var book = flow.ReadBook();
            if (book is not { State: BookState.Valid } || book.BidPrices.Length == 0 || book.AskPrices.Length == 0)
            {
                return frame with { Events = events };
            }
            return frame with
            {
                Mid = (book.BidPrices[0] + book.AskPrices[0]) / 2.0 / priceScale,
                Bids = Levels(book.BidPrices, book.BidAmounts, priceScale, amountScale),
                Asks = Levels(book.AskPrices, book.AskAmounts, priceScale, amountScale),
                Events = events,
            };
        }

        private static double Notional(in FlowEvent e) => Math.Abs((double)e.Amount) * e.Price;

        private static double[][] Levels(long[] prices, long[] amounts, double priceScale, double amountScale) =>
            [.. Enumerable.Range(0, Math.Min(DepthLevels, prices.Length)).Select(i => new[] { prices[i] / priceScale, amounts[i] / amountScale })];
    }
}
