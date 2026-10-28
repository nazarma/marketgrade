using System.Text.Json;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Flow;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Exchanges;

/// <summary>
/// Kraken v2 <c>book</c> messages. A snapshot replaces the book; after every message each side is cut to
/// the subscribed depth (Kraken sends no removal for levels that fall out of range) and the CRC32 of the
/// top 10 levels must match the message's <c>checksum</c>. Kraken writes <c>type</c> before <c>data</c>.
/// <c>trade</c> messages carry the taker's side.
/// </summary>
internal sealed class KrakenBookHandler(ExchangeAdapter adapter, PairRules rules, int capacity, ILogger? logger)
    : BookHandler(adapter, rules, capacity, logger)
{
    protected override HandleOutcome HandleCore(MessageBuffer message)
    {
        if (Kind == MessageKind.Trade)
        {
            return HandleTrades(message);
        }
        if (Kind != MessageKind.Data || Book.State == BookState.Invalid)
        {
            return HandleOutcome.Ignored;
        }

        var reader = new Utf8JsonReader(message.Span);
        Expect(ref reader, JsonTokenType.StartObject);
        bool? snapshot = null;
        uint checksum = 0;
        var hasChecksum = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                snapshot = reader.ValueTextEquals("snapshot"u8);
                if (snapshot == false && Book.State == BookState.Syncing)
                {
                    // Updates before the snapshot of this connection.
                    return HandleOutcome.Ignored;
                }
            }
            else if (reader.ValueTextEquals("data"u8))
            {
                if (snapshot is not { } isSnapshot)
                {
                    return Fail(FailureKind.Parse, "Book message has data before its type");
                }
                if (isSnapshot)
                {
                    Book.Clear();
                }
                Expect(ref reader, JsonTokenType.StartArray);
                while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                {
                    while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                    {
                        if (reader.ValueTextEquals("bids"u8))
                        {
                            ReadLevels(ref reader, Book.Bids);
                        }
                        else if (reader.ValueTextEquals("asks"u8))
                        {
                            ReadLevels(ref reader, Book.Asks);
                        }
                        else if (reader.ValueTextEquals("checksum"u8))
                        {
                            reader.Read();
                            checksum = reader.GetUInt32();
                            hasChecksum = true;
                        }
                        else if (reader.ValueTextEquals("timestamp"u8))
                        {
                            ExchangeUnixNs = ReadTimestamp(ref reader);
                        }
                        else
                        {
                            Skip(ref reader);
                        }
                    }
                }
            }
            else
            {
                Skip(ref reader);
            }
        }

        if (!hasChecksum)
        {
            return Fail(FailureKind.Parse, "Book message without checksum");
        }
        Book.Bids.Truncate(KrakenAdapter.BookDepth);
        Book.Asks.Truncate(KrakenAdapter.BookDepth);
        var computed = KrakenChecksum.Compute(Book);
        if (computed != checksum)
        {
            return Fail(FailureKind.Checksum, $"Checksum mismatch: Kraken sent {checksum}, book has {computed} (message {message.Seq})");
        }
        return Applied(message);
    }

    private HandleOutcome HandleTrades(MessageBuffer message)
    {
        try
        {
            var reader = new Utf8JsonReader(message.Span);
            Expect(ref reader, JsonTokenType.StartObject);
            var update = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    if (!reader.ValueTextEquals("update"u8))
                    {
                        // A snapshot holds trades from before we subscribed.
                        return HandleOutcome.Ignored;
                    }
                    update = true;
                }
                else if (reader.ValueTextEquals("data"u8))
                {
                    if (!update)
                    {
                        return TradeFailed();
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
            return HandleOutcome.Ignored;
        }
        catch (Exception e) when (IsReadError(e))
        {
            return TradeFailed();
        }
    }

    private void ReadTrade(ref Utf8JsonReader reader)
    {
        var rules = Book.Rules;
        FlowSide? side = null;
        long price = -1, qty = -1, time = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("side"u8))
            {
                reader.Read();
                side = reader.ValueTextEquals("buy"u8) ? FlowSide.Buy
                    : reader.ValueTextEquals("sell"u8) ? FlowSide.Sell
                    : throw new FormatException($"Unknown side at byte {reader.TokenStartIndex}");
            }
            else if (reader.ValueTextEquals("price"u8))
            {
                price = ReadDecimal(ref reader, rules.PriceDecimals);
            }
            else if (reader.ValueTextEquals("qty"u8))
            {
                qty = ReadDecimal(ref reader, rules.AmountDecimals);
            }
            else if (reader.ValueTextEquals("timestamp"u8))
            {
                time = ReadTimestamp(ref reader);
            }
            else
            {
                Skip(ref reader);
            }
        }
        if (side is not { } taker || price < 0 || qty < 0 || time == 0)
        {
            throw new FormatException("Trade without side, price, qty or timestamp");
        }
        AddTrade(time, taker, price, qty);
    }

    private void ReadLevels(ref Utf8JsonReader reader, OrderBookSide side)
    {
        var rules = Book.Rules;
        Expect(ref reader, JsonTokenType.StartArray);
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            long price = -1, qty = -1;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("price"u8))
                {
                    price = ReadDecimal(ref reader, rules.PriceDecimals);
                }
                else if (reader.ValueTextEquals("qty"u8))
                {
                    qty = ReadDecimal(ref reader, rules.AmountDecimals);
                }
                else
                {
                    Skip(ref reader);
                }
            }
            if (price < 0 || qty < 0)
            {
                throw new FormatException("Book level without price or qty");
            }
            side.Set(price, qty);
        }
    }
}
