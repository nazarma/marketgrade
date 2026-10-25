using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Threading.Channels;

namespace Marketgrade.Engine.Tests;

public class ExchangeConnectionTests
{
    private static readonly EngineOptions s_fast = new()
    {
        WatchdogInterval = TimeSpan.FromMilliseconds(20),
        DeadAfter = TimeSpan.FromMilliseconds(300),
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        BlockedRetryDelay = TimeSpan.FromMinutes(1),
        PoolSize = 16,
    };

    [Fact]
    public async Task Message_split_across_frames_arrives_as_one_message()
    {
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            await c.SendFragmentsAsync("{\"a\":", "\"split", " message\"", "}");
            await c.Closed;
        });
        await using var harness = Harness.Start(server, s_fast);

        var buffer = await harness.ReadAsync();

        Assert.Equal("{\"a\":\"split message\"}", Encoding.UTF8.GetString(buffer.Span));
        buffer.Release();
    }

    [Fact]
    public async Task Message_larger_than_the_buffer_arrives_intact()
    {
        var large = new byte[100_000];
        for (var i = 0; i < large.Length; i++)
        {
            large[i] = (byte)('a' + i % 26);
        }
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            await c.SendAsync(large);
            await c.SendAsync("small");
            await c.Closed;
        });
        await using var harness = Harness.Start(server, s_fast);

        var first = await harness.ReadAsync();
        Assert.True(first.Span.SequenceEqual(large));
        first.Release();
        var second = await harness.ReadAsync();
        Assert.Equal("small", Encoding.UTF8.GetString(second.Span));
        second.Release();

        await harness.DisposeAsync();
        Assert.Equal(harness.Pool.Created, harness.Pool.Available);
        var buffers = Enumerable.Range(0, harness.Pool.Created).Select(_ => harness.Pool.Rent()).ToList();
        Assert.All(buffers, b => Assert.Equal(s_fast.BufferSize, b.Bytes.Length));
    }

    [Fact]
    public async Task Silence_leads_to_reconnect_with_a_new_connection_id()
    {
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            await c.SendAsync($"connection {c.Index}");
            await c.Closed;
        });
        await using var harness = Harness.Start(server, s_fast);

        var first = await harness.ReadAsync();
        var second = await harness.ReadAsync();

        Assert.Equal(1, first.ConnectionId);
        Assert.Equal(2, second.ConnectionId);
        Assert.Equal("connection 1", Encoding.UTF8.GetString(second.Span));
        Assert.Contains(harness.Events.GetRecent(10), e => e.Event == "Dead after 300 ms silence");
        Assert.Equal(1, harness.Connection.ReconnectCount);
        first.Release();
        second.Release();
    }

    [Fact]
    public async Task Server_close_is_followed_by_reconnects_on_the_delay_sequence()
    {
        int[] delaysMs = [100, 200, 400];
        await using var server = await FakeExchangeServer.StartAsync(c => c.CloseAsync());
        await using var harness = Harness.Start(server, s_fast with
        {
            ReconnectDelays = [.. delaysMs.Select(ms => TimeSpan.FromMilliseconds(ms))],
        });

        await server.WaitForAttemptsAsync(5);
        var attempts = server.Attempts;

        int[] expectedMs = [100, 200, 400, 400];
        for (var i = 0; i < expectedMs.Length; i++)
        {
            var gap = Stopwatch.GetElapsedTime(attempts[i], attempts[i + 1]).TotalMilliseconds;
            Assert.InRange(gap, expectedMs[i], expectedMs[i] + 300);
        }
        Assert.Contains(harness.Events.GetRecent(10), e => e.Event == "Closed by server" && e.Detail!.StartsWith("1000 bye"));
    }

    [Fact]
    public async Task Http_451_blocks_the_connection_without_fast_retries()
    {
        await using var server = await FakeExchangeServer.StartAsync();
        server.RejectWithStatus = 451;
        await using var harness = Harness.Start(server, s_fast with { ReconnectDelays = [TimeSpan.FromMilliseconds(10)] });

        await server.WaitForAttemptsAsync(1);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Assert.Single(server.Attempts);
        Assert.Equal(ConnectionState.Blocked, harness.Connection.State);
        Assert.Equal(0, harness.Connection.ReconnectCount);
        Assert.Contains(harness.Events.GetRecent(10), e => e.Event == "Blocked (HTTP 451)");
    }

    [Fact]
    public async Task Subscribe_messages_are_sent_again_after_every_reconnect()
    {
        string[] subscribes = ["{\"subscribe\":\"book\"}", "{\"subscribe\":\"heartbeat\"}"];
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            await c.WaitForReceivedAsync(subscribes.Length);
            await c.CloseAsync();
        });
        await using var harness = Harness.Start(server, s_fast, subscribes);

        await server.WaitForConnectionsAsync(3);
        await harness.DisposeAsync();

        Assert.All(server.Connections, c => Assert.Equal(subscribes, c.Received));
    }

    [Fact]
    public async Task Seq_increases_by_one_per_message_across_reconnects()
    {
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            for (var i = 0; i < 5; i++)
            {
                await c.SendAsync($"message {i}");
            }
            await c.CloseAsync();
        });
        await using var harness = Harness.Start(server, s_fast);

        var seqs = new List<long>();
        var connectionIds = new List<long>();
        for (var i = 0; i < 10; i++)
        {
            var buffer = await harness.ReadAsync();
            seqs.Add(buffer.Seq);
            connectionIds.Add(buffer.ConnectionId);
            Assert.Equal(7, buffer.ExchangeId);
            Assert.InRange(buffer.ArrivalUnixNs, DateTime.UtcNow.AddMinutes(-1).Subtract(DateTime.UnixEpoch).Ticks * 100, long.MaxValue);
            buffer.Release();
        }

        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), seqs);
        Assert.Equal([1, 1, 1, 1, 1, 2, 2, 2, 2, 2], connectionIds);
    }

    [Fact]
    public async Task Scheduled_close_reconnects_without_counting_a_failure()
    {
        const string name = "ScheduledTest";
        var reasons = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == EngineMetrics.MeterName && instrument.Name == "reconnects")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var tagList = tags.ToArray();
            if (tagList.Any(t => t.Key == "exchange" && (string?)t.Value == name))
            {
                lock (reasons)
                {
                    reasons.Add((string)tagList.Single(t => t.Key == "reason").Value!);
                }
            }
        });
        listener.Start();

        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            if (c.Index == 0)
            {
                await Task.Delay(400);
                await c.CloseAsync();
            }
            await c.Closed;
        });
        var adapter = new TestAdapter(server.Uri, name) { Lifetime = TimeSpan.FromMilliseconds(300) };
        await using var harness = Harness.Start(adapter, s_fast with
        {
            DeadAfter = TimeSpan.FromSeconds(5),
            ScheduledCloseTolerance = TimeSpan.FromMilliseconds(50),
        });

        await server.WaitForConnectionsAsync(2);

        Assert.Equal(0, harness.Connection.ReconnectCount);
        Assert.Contains(harness.Events.GetRecent(10), e => e.Event == "Closed by server (300 ms limit)");
        lock (reasons)
        {
            Assert.Equal(["scheduled"], reasons);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Channel<MessageBuffer> _queue = Channel.CreateUnbounded<MessageBuffer>();
        private readonly EngineMetrics _metrics = new();
        private bool _stopped;

        private Harness(ExchangeAdapter adapter, EngineOptions options)
        {
            Pool = new MessageBufferPool(options.PoolSize, options.BufferSize);
            Connection = new ExchangeConnection(adapter, Pool, [_queue.Writer], options, Events, _metrics);
        }

        public MessageBufferPool Pool { get; }

        public ConnectionEventLog Events { get; } = new(200);

        public ExchangeConnection Connection { get; }

        public static Harness Start(FakeExchangeServer server, EngineOptions options, params string[] subscribes) =>
            Start(new TestAdapter(server.Uri, "Test", subscribes), options);

        public static Harness Start(ExchangeAdapter adapter, EngineOptions options)
        {
            var harness = new Harness(adapter, options);
            harness.Connection.Start();
            return harness;
        }

        public async Task<MessageBuffer> ReadAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var buffer = await _queue.Reader.ReadAsync(timeout.Token);
            Assert.True(buffer.Length > 0);
            return buffer;
        }

        public async ValueTask DisposeAsync()
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            await Connection.StopAsync();
            await foreach (var buffer in _queue.Reader.ReadAllAsync())
            {
                buffer.Release();
            }
            _metrics.Dispose();
        }
    }
}
