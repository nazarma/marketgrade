using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Marketgrade.Engine;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Benchmarks;

/// <summary>
/// Real update messages, the 500 that follow the first valid book in each exchange's recorded minute,
/// applied to that book. <see cref="Apply"/> is the handler alone: parse, apply and check.
/// <see cref="WorkerPath"/> is everything the book worker does per message: classify, handle (for
/// Coinbase including the hold time from every update's <c>event_time</c>), delay, catch-up, hold time,
/// queue wait and processing statistics, counters and the release of the buffer. Each iteration rebuilds the book from
/// the snapshot first, outside the measurement.
/// </summary>
[MemoryDiagnoser]
public class BookBenchmarks
{
    private const int Updates = 500;

    private List<(RecordHeader Header, byte[] Payload)> _records = null!;
    private MessageBufferPool _pool = null!;
    private readonly MessageBuffer[] _updates = new MessageBuffer[Updates];
    private BookHandler _handler = null!;
    private BookWorker _worker = null!;
    private HttpClient _http = null!;

    [Params("kraken", "coinbase", "binance")]
    public string Exchange { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _records = Fixtures.Records(Exchange);
        _pool = new MessageBufferPool(Updates + 64, new EngineOptions().BufferSize);
        _http = new HttpClient();
    }

    [GlobalCleanup]
    public void Cleanup() => _http.Dispose();

    [IterationSetup(Target = nameof(Apply))]
    public void SetupApply()
    {
        _handler = Fixtures.AdapterFor($"{Exchange}/").CreateBookHandler(Fixtures.RulesFor(Exchange), new EngineOptions())!;
        Sync(_handler, buffer => _handler.Handle(buffer) != HandleOutcome.Held);
    }

    [IterationCleanup(Target = nameof(Apply))]
    public void CleanupApply()
    {
        foreach (var buffer in _updates)
        {
            buffer.Release();
        }
    }

    [IterationSetup(Target = nameof(WorkerPath))]
    public void SetupWorker()
    {
        var adapter = Fixtures.AdapterFor($"{Exchange}/");
        var options = new EngineOptions();
        var events = new ConnectionEventLog(200);
        var queue = Channel.CreateUnbounded<MessageBuffer>();
        var connection = new ExchangeConnection(adapter, _pool, [queue.Writer], options, events);
        _worker = new BookWorker(adapter, connection, queue.Reader, new ManualResetEventSlim(), _pool, _http, new ClockOffset(),
            options, events, NullLogger.Instance);
        _worker.Prepare(Fixtures.RulesFor(Exchange));
        Sync(_worker.Handler!, buffer =>
        {
            _worker.Process(buffer);
            return false;
        });
    }

    [Benchmark(OperationsPerInvoke = Updates)]
    public long Apply()
    {
        long applied = 0;
        foreach (var buffer in _updates)
        {
            applied += _handler.Handle(buffer) == HandleOutcome.Applied ? 1 : 0;
        }
        return applied;
    }

    [Benchmark(OperationsPerInvoke = Updates)]
    public long WorkerPath()
    {
        foreach (var buffer in _updates)
        {
            _worker.Process(buffer);
        }
        return _worker.DataMessages;
    }

    /// <summary>
    /// Feeds records until the book is valid, as the live worker would (a Binance snapshot comes from the
    /// recorded REST response), then loads the next <see cref="Updates"/> messages into buffers.
    /// <paramref name="handle"/> returns true if the caller still has to release the buffer.
    /// </summary>
    private void Sync(BookHandler handler, Func<MessageBuffer, bool> handle)
    {
        var i = 0;
        for (; handler.Book.State != BookState.Valid; i++)
        {
            var (header, payload) = _records[i];
            if (header.Type == RecordType.Snapshot && handler is BinanceBookHandler binance)
            {
                var snapshot = Load(header, payload);
                binance.OnSnapshot(snapshot);
                snapshot.Release();
            }
            else if (header.Type == RecordType.Message)
            {
                if (handler is BinanceBookHandler { NeedsSnapshot: true } waiting)
                {
                    waiting.StartFetch(0);
                }
                var buffer = Load(header, payload);
                if (handle(buffer))
                {
                    buffer.Release();
                }
            }
        }
        for (var loaded = 0; loaded < Updates; i++)
        {
            var (header, payload) = _records[i];
            if (header.Type == RecordType.Message)
            {
                _updates[loaded++] = Load(header, payload);
            }
        }
    }

    private MessageBuffer Load(RecordHeader header, byte[] payload)
    {
        var buffer = _pool.Rent();
        while (buffer.Bytes.Length < payload.Length)
        {
            buffer.Grow(0);
        }
        payload.CopyTo(buffer.Bytes, 0);
        buffer.Length = payload.Length;
        buffer.Seq = header.Seq;
        buffer.ConnectionId = header.ConnectionId;
        buffer.ArrivalUnixNs = header.ArrivalUnixNs;
        buffer.Counter = 1;
        return buffer;
    }
}

/// <summary>The pieces every message goes through: decimal and timestamp parsing, and the Kraken checksum on a real book.</summary>
[MemoryDiagnoser]
public class ParsingBenchmarks
{
    private static readonly byte[][] s_decimals =
    [
        "80368.5"u8.ToArray(), "0.00116000"u8.ToArray(), "80470.00000000"u8.ToArray(), "0.00042000"u8.ToArray(),
    ];

    private static readonly byte[] s_timestamp = "2026-10-25T22:23:17.293696175Z"u8.ToArray();

    private OrderBook _book = null!;

    [GlobalSetup]
    public void Setup()
    {
        var handler = new KrakenAdapter().CreateBookHandler(Fixtures.RulesFor("kraken"), new EngineOptions())!;
        var pool = new MessageBufferPool(4, 16 * 1024);
        foreach (var (header, payload) in Fixtures.Records("kraken").Where(r => r.Header.Type == RecordType.Message).Take(500))
        {
            var buffer = pool.Rent();
            while (buffer.Bytes.Length < payload.Length)
            {
                buffer.Grow(0);
            }
            payload.CopyTo(buffer.Bytes, 0);
            buffer.Length = payload.Length;
            buffer.ConnectionId = header.ConnectionId;
            buffer.Counter = 1;
            handler.Handle(buffer);
            buffer.Release();
        }
        _book = handler.Book;
    }

    [Benchmark(OperationsPerInvoke = 4)]
    public long ParseDecimal()
    {
        long sum = 0;
        foreach (var text in s_decimals)
        {
            ScaledDecimal.TryParse(text, 8, out var value);
            sum += value;
        }
        return sum;
    }

    [Benchmark]
    public long ParseTimestamp()
    {
        Iso8601.TryParseUnixNs(s_timestamp, out var unixNs);
        return unixNs;
    }

    [Benchmark]
    public uint KrakenChecksum() => Engine.Books.KrakenChecksum.Compute(_book);
}
