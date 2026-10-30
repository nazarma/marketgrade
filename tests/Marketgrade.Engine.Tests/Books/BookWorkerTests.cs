using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Flow;
using Marketgrade.Engine.History;
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
        Assert.True(status.QueueWaitP99Ns >= status.QueueWaitP50Ns && status.QueueWaitP50Ns > 0);
        Assert.True(Decimal(status.BestBid) < Decimal(status.BestAsk));
        Assert.Equal(Decimal(status.BestAsk) - Decimal(status.BestBid), Decimal(status.Spread));
        Assert.Equal(KrakenAdapter.BookDepth, status.BidLevels);
        Assert.NotNull(status.ProcessingP50Ns);
        // The fake server sends everything right after the snapshot, so most of it is catch-up.
        Assert.True(status.CatchUpMessages > 0);
        Assert.NotNull(status.CatchUpMaxDelayMs);
        var events = engine.GetRecentEvents(50).Select(e => e.Event).ToList();
        Assert.Equal(["Book synced", "Book invalid", "Reconnect requested", "Book valid"],
            events.Where(e => e.StartsWith("Book", StringComparison.Ordinal) || e == "Reconnect requested"));

        await engine.StopAsync();
        Assert.Equal(engine.Pool.Created, engine.Pool.Available);

        // The gap, the reconnect it asked for, and the minutes so far are published for the history.
        Assert.True(engine.History.Gaps.TryRead(out var gap));
        Assert.Equal(GapKind.Checksum, gap.Kind);
        Assert.Equal("Reconnected and rebuilt the book from a new snapshot", gap.RepairAction);
        Assert.Equal(status.LastGap.Cause, gap.Cause);
        Assert.True(gap.DurationSeconds > 0);
        Assert.True(engine.History.Reconnects.TryRead(out var reconnect));
        Assert.StartsWith("Reconnect requested: Checksum mismatch", reconnect.Reason);
        var minutes = new List<MinuteStats>();
        while (engine.History.Minutes.TryRead(out var minute))
        {
            minutes.Add(minute);
        }
        Assert.Equal(engine.Status.Exchanges[0].DataMessages, minutes.Sum(m => m.BookUpdates));
        Assert.Equal(1, minutes.Sum(m => m.Reconnects));
        Assert.Equal(1, minutes.Sum(m => m.GapsStarted));
        Assert.All(minutes, m => Assert.InRange(m.SecondsValid + m.SecondsInvalid, 0, m.SecondsRunning + 1e-9));

        await engine.DisposeAsync();
        var records = ReadRecords(tempFolder.Path);
        Assert.Contains(records, r => r.Type == RecordType.BookReset && Encoding.UTF8.GetString(r.Payload).StartsWith("Checksum mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Trades_are_counted_without_a_book()
    {
        var trade = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kraken", "trade-update.json"));
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            for (var i = 0; i < 3; i++)
            {
                await c.SendAsync(trade);
            }
            await c.SendAsync(trade.Replace("\"sell\"", "\"up\"", StringComparison.Ordinal));
            await c.Closed;
        });
        await using var engine = new MarketEngine(s_fast, [new KrakenAdapter(server.Uri)]);
        engine.Start([s_kraken]);

        await WaitUntilAsync(() => engine.Status.Exchanges[0] is { TradeMessages: 4, TradeFailures: 1 });

        var status = engine.Status.Exchanges[0];
        Assert.Equal(3, status.Trades);
        Assert.Equal(0, status.DataMessages);
        Assert.Equal(0, status.ErrorMessages);
        Assert.Equal(BookState.Syncing, status.BookState);
    }

    [Fact]
    public async Task Flow_is_written_only_while_enabled_and_counted_always()
    {
        var lines = TestMessages.Sequence("kraken-book.jsonl");
        var trade = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "kraken", "trade-update.json"));
        var secondHalf = new TaskCompletionSource();
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            await c.SendAsync(lines[0]);
            for (var i = 2; i < 50; i++)
            {
                await c.SendAsync(lines[i]);
            }
            await c.SendAsync(trade);
            await secondHalf.Task;
            for (var i = 50; i < 100; i++)
            {
                await c.SendAsync(lines[i]);
            }
            await c.SendAsync(trade);
            await c.Closed;
        });
        await using var engine = new MarketEngine(s_fast, [new KrakenAdapter(server.Uri)]);
        var flow = engine.Flows[0];
        engine.Start([s_kraken]);

        await WaitUntilAsync(() => flow.Trades == 1);
        var changesBefore = flow.BookChanges;
        Assert.True(changesBefore > 0);
        Assert.Equal(0, flow.Ring.Published);

        engine.FlowEnabled = true;
        // The worker picks the switch up after its next batch or housekeeping pass.
        await WaitUntilAsync(() => flow.WritingRing);
        secondHalf.SetResult();
        await WaitUntilAsync(() => flow.Trades == 2);

        var events = new FlowEvent[flow.Ring.Capacity];
        var read = flow.Ring.Read(0, events);
        Assert.Equal(1, events[..read.Count].Count(e => e.Kind == FlowKind.Trade));
        Assert.InRange(events[..read.Count].Count(e => e.Kind == FlowKind.BookChange), 1, flow.BookChanges - changesBefore);
        Assert.Equal("Kraken", flow.Exchange);
        Assert.Equal(s_kraken, flow.Rules);
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
        await WaitUntilAsync(() => engine.Status.Exchanges[0].ExchangeHoldP50Ms is not null);
        Assert.True(engine.Status.Exchanges[0].ExchangeHoldP99Ms >= engine.Status.Exchanges[0].ExchangeHoldP50Ms);
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
        // Events arrive every 5 ms, so some land within 100 ms after the second snapshot was applied.
        Assert.True(status.CatchUpMessages > 0);
        Assert.NotNull(status.CatchUpMaxDelayMs);
        await engine.StopAsync();
        Assert.Equal(engine.Pool.Created, engine.Pool.Available);

        // Repaired on the same connection: a gap for the history, no reconnect.
        Assert.True(engine.History.Gaps.TryRead(out var gap));
        Assert.Equal(GapKind.Sequence, gap.Kind);
        Assert.Equal("Rebuilt the book from a new REST snapshot on the same connection", gap.RepairAction);
        Assert.False(engine.History.Reconnects.TryRead(out _));

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
        engine.SyncClock((DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100 + 600_000 * 1_000_000L, Stopwatch.GetTimestamp());
        engine.Start([s_binance]);
        await WaitUntilAsync(() => engine.Status is { ClockOffsetMs: not null, Exchanges: [{ DelayP50Ms: not null }] });

        Assert.InRange(engine.Status.ClockOffsetMs!.Value, -600_001, -599_999);
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
