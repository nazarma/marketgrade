using System.Text.Json;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Flow;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Exchanges;

/// <summary>
/// Binance diff depth events, kept in sync with a REST depth snapshot as Binance documents it:
/// <list type="number">
/// <item>While a snapshot is being fetched, events are held (their pooled buffers are kept).</item>
/// <item>The snapshot replaces the book; held and new events with <c>u &lt;= lastUpdateId</c> are dropped.</item>
/// <item>The first event applied must have <c>U &lt;= lastUpdateId + 1 &lt;= u</c>.</item>
/// <item>After that an event with <c>u</c> at or below the book's id is ignored, and one with
/// <c>U</c> above the book's id + 1 means events were missed: the book is invalid and resyncs.</item>
/// </list>
/// Fetching is up to the caller (<see cref="ShouldFetch"/>, <see cref="StartFetch"/>, <see cref="OnSnapshot"/>),
/// at most once per <paramref name="fetchInterval"/>. Binance writes <c>U</c> and <c>u</c> before <c>b</c> and <c>a</c>.
/// Trade events say whether the buyer was the maker (<c>m</c>); if so, the taker sold.
/// </summary>
internal sealed class BinanceBookHandler(ExchangeAdapter adapter, PairRules rules, int capacity, TimeSpan fetchInterval, ILogger? logger)
    : BookHandler(adapter, rules, capacity, logger)
{
    /// <summary>More held events are released and holding starts over.</summary>
    public const int MaxHeld = 2_000;

    private readonly MessageBuffer[] _held = new MessageBuffer[MaxHeld];
    private int _heldCount;
    private Phase _phase = Phase.NeedsSnapshot;
    private long _lastUpdateId;
    private long _nextFetchMs = long.MinValue;

    private enum Phase
    {
        /// <summary>Waiting until a fetch may start; events are dropped.</summary>
        NeedsSnapshot,

        /// <summary>A snapshot is on its way; events are held.</summary>
        Fetching,

        /// <summary>The snapshot is in the book; waiting for the event that continues it.</summary>
        AwaitingFirstEvent,

        Live,
    }

    public bool NeedsSnapshot => _phase == Phase.NeedsSnapshot;

    public int HeldCount => _heldCount;

    /// <summary>Snapshot fetches started.</summary>
    public long Resyncs { get; private set; }

    /// <summary>Times more than <see cref="MaxHeld"/> events piled up and the held ones were released.</summary>
    public long HeldOverflows { get; private set; }

    /// <summary>Held events the last <see cref="OnSnapshot"/> applied.</summary>
    public int HeldApplied { get; private set; }

    /// <summary>True when a snapshot is needed and the rate limit allows a fetch at <paramref name="nowMs"/>.</summary>
    public bool ShouldFetch(long nowMs) => _phase == Phase.NeedsSnapshot && nowMs >= _nextFetchMs;

    /// <summary>The caller is fetching a snapshot now: events are held from here on.</summary>
    public void StartFetch(long nowMs)
    {
        _phase = Phase.Fetching;
        _nextFetchMs = nowMs + (long)fetchInterval.TotalMilliseconds;
        Resyncs++;
    }

    /// <summary>The fetch failed; the next one waits at least until <paramref name="retryAtMs"/>.</summary>
    public void FetchFailed(long retryAtMs)
    {
        ReleaseHeld();
        _phase = Phase.NeedsSnapshot;
        _nextFetchMs = Math.Max(_nextFetchMs, retryAtMs);
    }

    /// <summary>
    /// Applies a REST depth snapshot, then the held events. The caller keeps and releases
    /// <paramref name="snapshot"/>; every held buffer is released here.
    /// </summary>
    public HandleOutcome OnSnapshot(MessageBuffer snapshot)
    {
        if (_phase != Phase.Fetching)
        {
            // Too late: the connection changed or the book failed meanwhile.
            return HandleOutcome.Ignored;
        }
        try
        {
            if (!ReadSnapshot(snapshot.Span))
            {
                return Fail(FailureKind.Parse, "Depth snapshot without lastUpdateId");
            }
            _phase = Phase.AwaitingFirstEvent;
            Book.LastArrivalUnixNs = snapshot.ArrivalUnixNs;

            var outcome = HandleOutcome.Ignored;
            var count = _heldCount;
            _heldCount = 0;
            HeldApplied = 0;
            for (var i = 0; i < count; i++)
            {
                var held = _held[i];
                _held[i] = null!;
                try
                {
                    if (outcome != HandleOutcome.Failed)
                    {
                        var result = HandleEvent(held);
                        if (result is HandleOutcome.Synced or HandleOutcome.Applied)
                        {
                            HeldApplied++;
                        }
                        if (result is HandleOutcome.Synced or HandleOutcome.Failed)
                        {
                            outcome = result;
                        }
                    }
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
                {
                    outcome = Fail(FailureKind.Parse, $"Held event could not be read: {e.Message}");
                }
                finally
                {
                    held.Release();
                }
            }
            return outcome;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return Fail(FailureKind.Parse, $"Depth snapshot could not be read: {e.Message}");
        }
    }

    /// <summary>Gives every held buffer back to the pool.</summary>
    public void ReleaseHeld()
    {
        for (var i = 0; i < _heldCount; i++)
        {
            _held[i].Release();
            _held[i] = null!;
        }
        _heldCount = 0;
    }

    protected override void ResetCore()
    {
        base.ResetCore();
        ReleaseHeld();
        _phase = Phase.NeedsSnapshot;
    }

    protected override void OnFailed()
    {
        ReleaseHeld();
        _phase = Phase.NeedsSnapshot;
    }

    protected override HandleOutcome HandleCore(MessageBuffer message) => Kind switch
    {
        MessageKind.Data => HandleEvent(message),
        MessageKind.Trade => HandleTrade(message),
        _ => HandleOutcome.Ignored,
    };

    private HandleOutcome HandleTrade(MessageBuffer message)
    {
        try
        {
            var rules = Book.Rules;
            var reader = new Utf8JsonReader(message.Span);
            Expect(ref reader, JsonTokenType.StartObject);
            bool? buyerIsMaker = null;
            long price = -1, quantity = -1, timeMs = 0;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("p"u8))
                {
                    price = ReadDecimal(ref reader, rules.PriceDecimals);
                }
                else if (reader.ValueTextEquals("q"u8))
                {
                    quantity = ReadDecimal(ref reader, rules.AmountDecimals);
                }
                else if (reader.ValueTextEquals("T"u8))
                {
                    reader.Read();
                    timeMs = reader.GetInt64();
                }
                else if (reader.ValueTextEquals("m"u8))
                {
                    reader.Read();
                    buyerIsMaker = reader.GetBoolean();
                }
                else
                {
                    Skip(ref reader);
                }
            }
            if (buyerIsMaker is not { } maker || price < 0 || quantity < 0 || timeMs <= 0)
            {
                return TradeFailed();
            }
            AddTrade(timeMs * 1_000_000, maker ? FlowSide.Sell : FlowSide.Buy, price, quantity);
            return HandleOutcome.Ignored;
        }
        catch (Exception e) when (IsReadError(e))
        {
            return TradeFailed();
        }
    }

    private HandleOutcome HandleEvent(MessageBuffer message)
    {
        StartMessage(message);
        var reader = new Utf8JsonReader(message.Span);
        Expect(ref reader, JsonTokenType.StartObject);
        long first = -1, last = -1;
        var decided = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("E"u8))
            {
                reader.Read();
                ExchangeUnixNs = reader.GetInt64() * 1_000_000;
            }
            else if (reader.ValueTextEquals("U"u8))
            {
                reader.Read();
                first = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("u"u8))
            {
                reader.Read();
                last = reader.GetInt64();
            }
            else if (reader.ValueTextEquals("b"u8) || reader.ValueTextEquals("a"u8))
            {
                var side = reader.ValueTextEquals("b"u8) ? Book.Bids : Book.Asks;
                if (!decided)
                {
                    if (Decide(message, first, last) is { } outcome)
                    {
                        return outcome;
                    }
                    decided = true;
                }
                ReadLevels(ref reader, side, snapshot: false);
            }
            else
            {
                Skip(ref reader);
            }
        }
        if (!decided && Decide(message, first, last) is { } early)
        {
            return early;
        }
        _lastUpdateId = last;
        _phase = Phase.Live;
        return Applied(message);
    }

    /// <summary>Null if the event is to be applied; otherwise what happens to it instead.</summary>
    private HandleOutcome? Decide(MessageBuffer message, long first, long last)
    {
        if (first < 0 || last < first)
        {
            throw new FormatException("Depth event without valid U and u before its levels");
        }
        switch (_phase)
        {
            case Phase.NeedsSnapshot:
                return HandleOutcome.Ignored;
            case Phase.Fetching:
                if (_heldCount == MaxHeld)
                {
                    ReleaseHeld();
                    HeldOverflows++;
                }
                _held[_heldCount++] = message;
                return HandleOutcome.Held;
            case Phase.AwaitingFirstEvent:
                if (last <= _lastUpdateId)
                {
                    return HandleOutcome.Ignored;
                }
                if (first > _lastUpdateId + 1)
                {
                    return Fail(FailureKind.Sequence,
                        $"First event after the snapshot starts at U {first}, needs at most {_lastUpdateId + 1} (message {message.Seq})");
                }
                return null;
            default:
                if (last <= _lastUpdateId)
                {
                    return HandleOutcome.Ignored;
                }
                if (first > _lastUpdateId + 1)
                {
                    return Fail(FailureKind.Sequence,
                        $"Update ID gap: expected U {_lastUpdateId + 1}, got {first} (message {message.Seq})");
                }
                return null;
        }
    }

    /// <summary>Reads <c>lastUpdateId</c>, <c>bids</c> and <c>asks</c> in any order into a cleared book.</summary>
    private bool ReadSnapshot(ReadOnlySpan<byte> json)
    {
        Book.Clear();
        var reader = new Utf8JsonReader(json);
        Expect(ref reader, JsonTokenType.StartObject);
        var hasId = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("lastUpdateId"u8))
            {
                reader.Read();
                _lastUpdateId = reader.GetInt64();
                hasId = true;
            }
            else if (reader.ValueTextEquals("bids"u8))
            {
                ReadLevels(ref reader, Book.Bids, snapshot: true);
            }
            else if (reader.ValueTextEquals("asks"u8))
            {
                ReadLevels(ref reader, Book.Asks, snapshot: true);
            }
            else
            {
                Skip(ref reader);
            }
        }
        return hasId;
    }

    /// <summary>Levels are <c>["price", "quantity"]</c> pairs; quantity 0 removes the level.</summary>
    private void ReadLevels(ref Utf8JsonReader reader, OrderBookSide side, bool snapshot)
    {
        var rules = Book.Rules;
        Expect(ref reader, JsonTokenType.StartArray);
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            var price = ReadDecimal(ref reader, rules.PriceDecimals);
            var quantity = ReadDecimal(ref reader, rules.AmountDecimals);
            Expect(ref reader, JsonTokenType.EndArray);
            if (snapshot)
            {
                side.Set(price, quantity);
            }
            else
            {
                SetLevel(side, price, quantity);
            }
        }
    }
}
