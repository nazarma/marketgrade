using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Marketgrade.Engine.Books;

public enum HandleOutcome
{
    /// <summary>Nothing changed: not book data, or a message the book must skip (before a snapshot, after a failure).</summary>
    Ignored,

    /// <summary>Changes were applied and the book passed its check.</summary>
    Applied,

    /// <summary>A snapshot was applied and checked: the book is valid again.</summary>
    Synced,

    /// <summary>Kept until the book can use it (Binance during a resync); the handler now owns the buffer.</summary>
    Held,

    /// <summary>A check failed or the message could not be read: the book is invalid, see <see cref="BookHandler.Cause"/>.</summary>
    Failed,
}

public enum FailureKind
{
    None,
    Parse,
    Checksum,
    Sequence,
}

/// <summary>
/// Parses one exchange's messages straight into its <see cref="OrderBook"/> and runs the exchange's own
/// check after each one. Used by the live book worker and by replay alike, single-threaded. Handling
/// a message never allocates unless it fails.
/// </summary>
public abstract class BookHandler
{
    private long _connectionId = -1;

    protected BookHandler(ExchangeAdapter adapter, PairRules rules, int capacity, ILogger? logger)
    {
        Adapter = adapter;
        Book = new OrderBook(rules, capacity, adapter.Name, logger);
    }

    public ExchangeAdapter Adapter { get; }

    public OrderBook Book { get; }

    /// <summary>Kind of the last message handled.</summary>
    public MessageKind Kind { get; private set; }

    /// <summary>The exchange's timestamp of the last message, Unix nanoseconds, or 0 if it had none.</summary>
    public long ExchangeUnixNs { get; protected set; }

    public FailureKind Failure { get; private set; }

    /// <summary>Why the last message failed, including the numbers involved.</summary>
    public string? Cause { get; private set; }

    public HandleOutcome Handle(MessageBuffer message)
    {
        ExchangeUnixNs = 0;
        Kind = Adapter.Classify(message.Span);
        if (message.ConnectionId != _connectionId)
        {
            _connectionId = message.ConnectionId;
            ResetCore();
        }
        try
        {
            return HandleCore(message);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return Fail(FailureKind.Parse, $"Message could not be read: {e.Message}");
        }
    }

    /// <summary>Starts over from the next snapshot, as after a new connection.</summary>
    public void Reset()
    {
        _connectionId = -1;
        ResetCore();
    }

    /// <summary>Marks the book invalid from outside, for example when the connection is lost.</summary>
    public void Invalidate() => Book.State = BookState.Invalid;

    /// <summary>Called on a new connection; the default clears the book and waits for a snapshot.</summary>
    protected virtual void ResetCore()
    {
        Book.Clear();
        Book.State = BookState.Syncing;
    }

    protected abstract HandleOutcome HandleCore(MessageBuffer message);

    protected HandleOutcome Fail(FailureKind kind, string cause)
    {
        Failure = kind;
        Cause = cause;
        Book.State = BookState.Invalid;
        return HandleOutcome.Failed;
    }

    protected HandleOutcome Applied(MessageBuffer message)
    {
        Book.LastArrivalUnixNs = message.ArrivalUnixNs;
        if (ExchangeUnixNs != 0)
        {
            Book.LastExchangeUnixNs = ExchangeUnixNs;
        }
        if (Book.State == BookState.Valid)
        {
            return HandleOutcome.Applied;
        }
        Book.State = BookState.Valid;
        return HandleOutcome.Synced;
    }

    /// <summary>Reads a price or amount given as a JSON number or string.</summary>
    protected static long ReadDecimal(ref Utf8JsonReader reader, int decimals)
    {
        reader.Read();
        if (reader.TokenType is not (JsonTokenType.Number or JsonTokenType.String) || reader.ValueIsEscaped
            || !ScaledDecimal.TryParse(reader.ValueSpan, decimals, out var value))
        {
            throw new FormatException($"Invalid decimal at byte {reader.TokenStartIndex}");
        }
        return value;
    }

    protected static long ReadTimestamp(ref Utf8JsonReader reader)
    {
        reader.Read();
        if (reader.TokenType != JsonTokenType.String || reader.ValueIsEscaped
            || !Iso8601.TryParseUnixNs(reader.ValueSpan, out var unixNs))
        {
            throw new FormatException($"Invalid timestamp at byte {reader.TokenStartIndex}");
        }
        return unixNs;
    }

    protected static void Expect(ref Utf8JsonReader reader, JsonTokenType type)
    {
        reader.Read();
        if (reader.TokenType != type)
        {
            throw new FormatException($"Expected {type} at byte {reader.TokenStartIndex}, found {reader.TokenType}");
        }
    }

    protected static void Skip(ref Utf8JsonReader reader)
    {
        reader.Read();
        reader.Skip();
    }
}
