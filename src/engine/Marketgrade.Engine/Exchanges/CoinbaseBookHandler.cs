using System.Text.Json;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Flow;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Exchanges;

/// <summary>
/// Coinbase Advanced Trade <c>level2</c>. <c>sequence_num</c> counts every message of the connection,
/// whatever its channel (heartbeats and subscriptions too, measured on a recording), so every message
/// must carry the previous one's number plus 1. The first number of a connection is taken as the start.
/// A snapshot event replaces the book; <c>new_quantity</c> is the new size, 0 removes the level.
/// Each update carries the <c>event_time</c> of the change; the envelope <c>timestamp</c> minus the oldest
/// one is how long Coinbase held the message (not for snapshots, whose times are the levels' history).
/// <c>market_trades</c> messages are part of the same sequence. Their <c>side</c> is the maker's (Coinbase
/// documents it so, and live trades match it), so the taker's side is the other one.
/// </summary>
internal sealed class CoinbaseBookHandler(ExchangeAdapter adapter, PairRules rules, int capacity, ILogger? logger)
    : BookHandler(adapter, rules, capacity, logger)
{
    private long _lastSequence = -1;
    private long _oldestEventTime;

    protected override void ResetCore()
    {
        base.ResetCore();
        _lastSequence = -1;
    }

    protected override HandleOutcome HandleCore(MessageBuffer message)
    {
        // After a failure the sequence is not followed until the next connection; trades still are.
        var checking = Book.State != BookState.Invalid;
        if (Kind == MessageKind.Error || (!checking && Kind != MessageKind.Trade))
        {
            return HandleOutcome.Ignored;
        }

        var data = Kind == MessageKind.Data;
        var trades = Kind == MessageKind.Trade;
        var hasSequence = false;
        var snapshot = false;
        _oldestEventTime = long.MaxValue;
        var reader = new Utf8JsonReader(message.Span);
        Expect(ref reader, JsonTokenType.StartObject);
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("sequence_num"u8))
            {
                reader.Read();
                var sequence = reader.GetInt64();
                hasSequence = true;
                if (!checking)
                {
                    continue;
                }
                if (_lastSequence >= 0 && sequence != _lastSequence + 1)
                {
                    var expected = _lastSequence + 1;
                    _lastSequence = sequence;
                    return Fail(FailureKind.Sequence, $"Sequence gap: expected {expected}, got {sequence} (message {message.Seq})");
                }
                _lastSequence = sequence;
            }
            else if (data && reader.ValueTextEquals("timestamp"u8))
            {
                ExchangeUnixNs = ReadTimestamp(ref reader);
            }
            else if (data && reader.ValueTextEquals("events"u8))
            {
                snapshot |= ReadEvents(ref reader);
            }
            else if (trades && reader.ValueTextEquals("events"u8))
            {
                try
                {
                    ReadTradeEvents(ref reader);
                }
                catch (Exception e) when (IsReadError(e))
                {
                    // Before its sequence_num the message's place in the connection is unknown.
                    return hasSequence || !checking
                        ? TradeFailed()
                        : Fail(FailureKind.Parse, $"Trade message could not be read before its sequence_num: {e.Message}");
                }
            }
            else
            {
                Skip(ref reader);
            }
        }

        if (!data)
        {
            return HandleOutcome.Ignored;
        }
        if (!hasSequence)
        {
            return Fail(FailureKind.Parse, "Book message without sequence_num");
        }
        if (!snapshot && Book.State == BookState.Syncing)
        {
            return HandleOutcome.Ignored;
        }
        if (!snapshot && _oldestEventTime != long.MaxValue && ExchangeUnixNs != 0)
        {
            ExchangeHoldNs = ExchangeUnixNs - _oldestEventTime;
        }
        return Applied(message);
    }

    /// <summary>Applies the events; true if one of them was a snapshot.</summary>
    private bool ReadEvents(ref Utf8JsonReader reader)
    {
        var snapshot = false;
        Expect(ref reader, JsonTokenType.StartArray);
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            bool? isSnapshot = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    isSnapshot = reader.ValueTextEquals("snapshot"u8);
                    if (isSnapshot == true)
                    {
                        Book.Clear();
                        snapshot = true;
                    }
                }
                else if (reader.ValueTextEquals("updates"u8))
                {
                    if (isSnapshot is null)
                    {
                        throw new FormatException("Event has updates before its type");
                    }
                    if (isSnapshot == false && Book.State == BookState.Syncing && !snapshot)
                    {
                        Skip(ref reader);
                    }
                    else
                    {
                        ReadUpdates(ref reader, update: isSnapshot == false);
                    }
                }
                else
                {
                    Skip(ref reader);
                }
            }
        }
        return snapshot;
    }

    /// <summary>Trades of <c>update</c> events; a <c>snapshot</c> event holds trades from before we subscribed.</summary>
    private void ReadTradeEvents(ref Utf8JsonReader reader)
    {
        Expect(ref reader, JsonTokenType.StartArray);
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            bool? update = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    update = reader.ValueTextEquals("update"u8);
                }
                else if (reader.ValueTextEquals("trades"u8))
                {
                    if (update is null)
                    {
                        throw new FormatException("Event has trades before its type");
                    }
                    if (update == false)
                    {
                        Skip(ref reader);
                        continue;
                    }
                    Expect(ref reader, JsonTokenType.StartArray);
                    while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        ReadTrade(ref reader);
                    }
                }
                else
                {
                    Skip(ref reader);
                }
            }
        }
    }

    private void ReadTrade(ref Utf8JsonReader reader)
    {
        var rules = Book.Rules;
        FlowSide? taker = null;
        long price = -1, size = -1, time = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("side"u8))
            {
                reader.Read();
                taker = reader.ValueTextEquals("BUY"u8) ? FlowSide.Sell
                    : reader.ValueTextEquals("SELL"u8) ? FlowSide.Buy
                    : throw new FormatException($"Unknown side at byte {reader.TokenStartIndex}");
            }
            else if (reader.ValueTextEquals("price"u8))
            {
                price = ReadDecimal(ref reader, rules.PriceDecimals);
            }
            else if (reader.ValueTextEquals("size"u8))
            {
                size = ReadDecimal(ref reader, rules.AmountDecimals);
            }
            else if (reader.ValueTextEquals("time"u8))
            {
                time = ReadTimestamp(ref reader);
            }
            else
            {
                Skip(ref reader);
            }
        }
        if (taker is not { } side || price < 0 || size < 0 || time == 0)
        {
            throw new FormatException("Trade without side, price, size or time");
        }
        AddTrade(time, side, price, size);
    }

    /// <param name="update">Not a snapshot: changes the book level by level and keeps the oldest <c>event_time</c>
    /// for the hold time; one that cannot be read is left out, it does not touch the book.</param>
    private void ReadUpdates(ref Utf8JsonReader reader, bool update)
    {
        var rules = Book.Rules;
        Expect(ref reader, JsonTokenType.StartArray);
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            OrderBookSide? side = null;
            long price = -1, quantity = -1;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("side"u8))
                {
                    reader.Read();
                    side = reader.ValueTextEquals("bid"u8) ? Book.Bids
                        : reader.ValueTextEquals("offer"u8) ? Book.Asks
                        : throw new FormatException($"Unknown side at byte {reader.TokenStartIndex}");
                }
                else if (reader.ValueTextEquals("price_level"u8))
                {
                    price = ReadDecimal(ref reader, rules.PriceDecimals);
                }
                else if (reader.ValueTextEquals("new_quantity"u8))
                {
                    quantity = ReadDecimal(ref reader, rules.AmountDecimals);
                }
                else if (update && reader.ValueTextEquals("event_time"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped
                        && Iso8601.TryParseUnixNs(reader.ValueSpan, out var eventTime) && eventTime < _oldestEventTime)
                    {
                        _oldestEventTime = eventTime;
                    }
                }
                else
                {
                    Skip(ref reader);
                }
            }
            if (side is null || price < 0 || quantity < 0)
            {
                throw new FormatException("Update without side, price_level or new_quantity");
            }
            if (update)
            {
                SetLevel(side, price, quantity);
            }
            else
            {
                side.Set(price, quantity);
            }
        }
    }
}
