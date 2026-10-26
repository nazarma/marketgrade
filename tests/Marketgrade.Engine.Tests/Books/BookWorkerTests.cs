using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Recording;
using Marketgrade.Engine.Tests.Recording;

namespace Marketgrade.Engine.Tests.Books;

/// <summary>The engine with real adapters against the fake exchange server: checks, gaps and repairs end to end.</summary>
public class BookWorkerTests
{
    private static readonly EngineOptions s_fast = new()
    {
        WatchdogInterval = TimeSpan.FromMilliseconds(20),
        DeadAfter = TimeSpan.FromSeconds(5),
        ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        StatusInterval = TimeSpan.FromMilliseconds(50),
        PoolSize = 64,
        BinanceResyncInterval = TimeSpan.FromMilliseconds(100),
        BookHousekeepingInterval = TimeSpan.FromMilliseconds(20),
    };

    private static readonly PairRules s_kraken = new(1, "BTC/USD", 1, 8);
    private static readonly PairRules s_coinbase = new(2, "BTC-USD", 2, 8);
    private static readonly PairRules s_binance = new(3, "BTCUSDT", 2, 5);

    [Fact]
    public async Task Kraken_checksum_mismatch_reconnects_and_the_new_snapshot_makes_the_book_valid()
    {
        var lines = TestMessages.Sequence("kraken-book.jsonl");
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            await c.SendAsync(lines[0]);
            await c.SendAsync(lines[2]);
            if (c.Index == 0)
            {
                // A new best bid Kraken's checksum does not include.
                await c.SendAsync(lines[3].Replace("\"bids\":[", "\"bids\":[{\"price\":80368.8,\"qty\":0.5},", StringComparison.Ordinal));
            }
            for (var i = 3; i < 100; i++)
            {
                await c.SendAsync(lines[i]);
            }
            await c.Closed;
        });
        using var tempFolder = new TempFolder();
        var engine = new MarketEngine(s_fast, [new KrakenAdapter(server.Uri)], recording: RecordingSettings.Default(tempFolder.Path));
        engine.Start([s_kraken]);

        // Percentiles are published once per second.
        await WaitUntilAsync(() => engine.Status.Exchanges[0] is { BookState: BookState.Valid, LastGap.EndUtc: not null, ProcessingP50Ns: not null, DataMessages: > 90 });

        var status = engine.Status.Exchanges[0];
        Assert.Equal(1, status.GapsToday);
        Assert.StartsWith("Checksum mismatch: Kraken sent", status.LastGap!.Cause);
        Assert.Equal(2, server.Connections.Count);
        Assert.True(status.ProcessingP99Ns >= status.ProcessingP50Ns);
        Assert.True(Decimal(status.BestBid) < Decimal(status.BestAsk));
        Assert.Equal(Decimal(status.BestAsk) - Decimal(status.BestBid), Decimal(status.Spread));
        Assert.Equal(KrakenAdapter.BookDepth, status.BidLevels);
        Assert.NotNull(status.ProcessingP50Ns);
        Assert.NotNull(status.DelayP50Ms);
        var events = engine.GetRecentEvents(50).Select(e => e.Event).ToList();
        Assert.Equal(["Book synced", "Book invalid", "Reconnect requested", "Book valid"],
            events.Where(e => e.StartsWith("Book", StringComparison.Ordinal) || e == "Reconnect requested"));

        await engine.StopAsync();
        Assert.Equal(engine.Pool.Created, engine.Pool.Available);
        await engine.DisposeAsync();
        var records = ReadRecords(tempFolder.Path);
        Assert.Contains(records, r => r.Type == RecordType.BookReset && Encoding.UTF8.GetString(r.Payload).StartsWith("Checksum mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Coinbase_sequence_gap_reconnects()
    {
        var snapshot = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "coinbase", "data-l2-snapshot-trimmed.json"));
        var recorded = TestMessages.Sequence("coinbase-after-snapshot.jsonl");
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            await c.SendAsync(snapshot);
            for (var i = 0; i < 40; i++)
            {
                if (c.Index == 0 && i == 10)
                {
                    continue;
                }
                await c.SendAsync(recorded[i]);
            }
            await c.Closed;
        });
        await using var engine = new MarketEngine(s_fast, [new CoinbaseAdapter(server.Uri)]);
        engine.Start([s_coinbase]);

        await WaitUntilAsync(() => engine.Status.Exchanges[0] is { BookState: BookState.Valid, LastGap.EndUtc: not null });

        Assert.Equal("Sequence gap: expected 11, got 12 (message 12)", engine.Status.Exchanges[0].LastGap!.Cause);
        Assert.Equal(2, server.Connections.Count);
    }

    [Fact]
    public async Task Binance_gap_resyncs_from_a_new_rest_snapshot_on_the_same_connection()
    {
        using var exchange = new FakeBinance();
        await using var server = await FakeExchangeServer.StartAsync(exchange.StreamAsync);
        var rest = new StubHttpHandler(exchange.SnapshotAsync);
        using var tempFolder = new TempFolder();
        var engine = new MarketEngine(s_fast, [new BinanceAdapter(server.Uri, new Uri("https://rest.test/"))],
            recording: RecordingSettings.Default(tempFolder.Path), httpHandler: rest);
        engine.Start([s_binance]);
        await WaitUntilAsync(() => engine.Status.Exchanges[0].BookState == BookState.Valid);
        Assert.Equal("https://rest.test/api/v3/depth?symbol=BTCUSDT&limit=5000", rest.LastUri!.AbsoluteUri);

        exchange.SkipIds = 5;
        await WaitUntilAsync(() => engine.Status.Exchanges[0] is { BookState: BookState.Valid, LastGap.EndUtc: not null });

        var status = engine.Status.Exchanges[0];
        Assert.StartsWith("Update ID gap: expected U ", status.LastGap!.Cause);
        Assert.Single(server.Connections);
        Assert.Equal(2, rest.Requests);
        Assert.Equal("100.00", status.BestBid);
        Assert.Equal("100.50", status.BestAsk);
        Assert.Equal("0.50", status.Spread);
        await engine.StopAsync();
        Assert.Equal(engine.Pool.Created, engine.Pool.Available);
        await engine.DisposeAsync();
        var records = ReadRecords(tempFolder.Path);
        Assert.Equal(2, records.Count(r => r.Type == RecordType.Snapshot && Encoding.UTF8.GetString(r.Payload).StartsWith("{\"lastUpdateId\":", StringComparison.Ordinal)));
        Assert.Single(records, r => r.Type == RecordType.BookReset);
    }

    [Fact]
    public async Task Book_snapshots_are_published_once_someone_reads_them()
    {
        using var exchange = new FakeBinance();
        await using var server = await FakeExchangeServer.StartAsync(exchange.StreamAsync);
        await using var engine = new MarketEngine(s_fast, [new BinanceAdapter(server.Uri, new Uri("https://rest.test/"))],
            httpHandler: new StubHttpHandler(exchange.SnapshotAsync));
        engine.Start([s_binance]);
        await WaitUntilAsync(() => engine.Status.Exchanges[0].BookState == BookState.Valid);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Null(engine.GetBook("BINANCE"));

        await WaitUntilAsync(() => engine.GetBook("binance") is not null);

        var book = engine.GetBook("binance")!;
        Assert.Equal(("Binance", "BTCUSDT", BookState.Valid), (book.Exchange, book.Pair, book.State));
        Assert.Equal([10000L, 9900L], book.BidPrices);
        Assert.Equal([10050L, 10100L], book.AskPrices);
        Assert.Equal(200_000L, book.AskAmounts[0]);
        Assert.Null(engine.GetBook("nope"));
        Assert.True(engine.HasExchange("Binance"));
    }

    [Fact]
    public async Task Binance_backs_off_after_http_429()
    {
        using var exchange = new FakeBinance();
        await using var server = await FakeExchangeServer.StartAsync(exchange.StreamAsync);
        var times = new List<long>();
        var rest = new StubHttpHandler(async (request, token) =>
        {
            lock (times)
            {
                times.Add(Environment.TickCount64);
                if (times.Count == 1)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                    return limited;
                }
            }
            return await exchange.SnapshotAsync(request, token);
        });
        await using var engine = new MarketEngine(s_fast, [new BinanceAdapter(server.Uri, new Uri("https://rest.test/"))], httpHandler: rest);
        engine.Start([s_binance]);

        await WaitUntilAsync(() => engine.Status.Exchanges[0].BookState == BookState.Valid);

        Assert.Equal(2, times.Count);
        Assert.InRange(times[1] - times[0], 900, 5_000);
        Assert.Contains(engine.GetRecentEvents(50), e => e.Event == "Snapshot rate limited" && e.Detail == "HTTP 429, retry in 1 s");
    }

    [Fact]
    public async Task Stopping_returns_every_buffer_including_held_binance_events()
    {
        using var exchange = new FakeBinance();
        await using var server = await FakeExchangeServer.StartAsync(exchange.StreamAsync);
        // The snapshot never arrives, so the worker keeps holding events until it stops.
        var rest = new StubHttpHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        });
        var engine = new MarketEngine(s_fast, [new BinanceAdapter(server.Uri, new Uri("https://rest.test/"))], httpHandler: rest);
        engine.Start([s_binance]);
        await WaitUntilAsync(() => rest.Requests == 1 && engine.Status.Exchanges[0].DataMessages > 30);
        Assert.True(engine.Pool.Available < engine.Pool.Created - 20);

        await engine.StopAsync();

        Assert.Equal(engine.Pool.Created, engine.Pool.Available);
        Assert.Equal(BookState.Syncing, engine.Status.Exchanges[0].BookState);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Clock_offset_corrects_the_delay_and_shows_in_the_status()
    {
        using var exchange = new FakeBinance();
        await using var server = await FakeExchangeServer.StartAsync(exchange.StreamAsync);
        await using var engine = new MarketEngine(s_fast, [new BinanceAdapter(server.Uri, new Uri("https://rest.test/"))],
            httpHandler: new StubHttpHandler(exchange.SnapshotAsync));
        Assert.Null(engine.Status.ClockOffsetMs);

        // As if this clock ran 10 minutes behind: every corrected delay grows by that much.
        engine.SetClockOffset(TimeSpan.FromMinutes(-10));
        engine.Start([s_binance]);
        await WaitUntilAsync(() => engine.Status is { ClockOffsetMs: not null, Exchanges: [{ DelayP50Ms: not null }] });

        Assert.Equal(-600_000, engine.Status.ClockOffsetMs);
        Assert.InRange(engine.Status.Exchanges[0].DelayP50Ms!.Value, 590_000, 610_000);
    }

    [Fact]
    public async Task Exchange_without_pair_rules_keeps_no_book_but_counts_messages()
    {
        var lines = TestMessages.Sequence("kraken-book.jsonl");
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            foreach (var line in lines.Take(20))
            {
                await c.SendAsync(line);
            }
            await c.Closed;
        });
        await using var engine = new MarketEngine(s_fast, [new KrakenAdapter(server.Uri)]);
        engine.Start([]);

        await WaitUntilAsync(() => engine.Status.Exchanges[0].DataMessages == 18);

        var status = engine.Status.Exchanges[0];
        Assert.Equal(BookState.NoPairRules, status.BookState);
        Assert.Null(status.BestBid);
        Assert.Null(status.ProcessingP50Ns);
    }

    private static decimal Decimal(string? text) => decimal.Parse(text!, CultureInfo.InvariantCulture);

    private static List<TestRecord> ReadRecords(string folder) =>
        [.. RecordingFiles.Enumerate(folder).OrderBy(f => f.Hour).SelectMany(f => TestRecordings.Read(f.Path))];

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>A diff depth stream with consecutive update IDs and a REST snapshot at the latest ID.</summary>
    private sealed class FakeBinance : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private long _lastId = 1_000;

        /// <summary>The next event skips this many IDs, once.</summary>
        public int SkipIds { get; set; }

        public async Task StreamAsync(FakeConnection connection)
        {
            try
            {
                while (!connection.Closed.IsCompleted && !_stop.IsCancellationRequested)
                {
                    var skip = SkipIds;
                    SkipIds = 0;
                    var last = Interlocked.Add(ref _lastId, 3 + skip);
                    var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await connection.SendAsync(
                        $$"""{"e":"depthUpdate","E":{{time}},"s":"BTCUSDT","U":{{last - 2}},"u":{{last}},"b":[["100.00","1.00000000"]],"a":[["100.50","2.00000000"]]}""");
                    await Task.Delay(5);
                }
            }
            catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or IOException or ObjectDisposedException)
            {
            }
        }

        public Task<HttpResponseMessage> SnapshotAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = $$"""{"lastUpdateId":{{Interlocked.Read(ref _lastId)}},"bids":[["99.00","1.00000"]],"asks":[["101.00","1.00000"]]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }

        public void Dispose() => _stop.Cancel();
    }
}
