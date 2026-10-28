using System.Text.Json;
using Marketgrade.Engine.Books;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Exchanges;

/// <summary>
/// Coinbase Advanced Trade <c>level2</c>. <c>sequence_num</c> counts every message of the connection,
/// whatever its channel (heartbeats and subscriptions too, measured on a recording), so every message
/// must carry the previous one's number plus 1. The first number of a connection is taken as the start.
/// A snapshot event replaces the book; <c>new_quantity</c> is the new size, 0 removes the level.
/// Each update carries the <c>event_time</c> of the change; the envelope <c>timestamp</c> minus the oldest
/// one is how long Coinbase held the message (not for snapshots, whose times are the levels' history).
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
        if (Kind == MessageKind.Error || Book.State == BookState.Invalid)
        {
            return HandleOutcome.Ignored;
        }

        var data = Kind == MessageKind.Data;
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
                        ReadUpdates(ref reader, holdTime: isSnapshot == false);
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

    /// <param name="holdTime">Keeps the oldest <c>event_time</c>; one that cannot be read is left out, it does not touch the book.</param>
    private void ReadUpdates(ref Utf8JsonReader reader, bool holdTime)
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
                else if (holdTime && reader.ValueTextEquals("event_time"u8))
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
            side.Set(price, quantity);
        }
    }
}
