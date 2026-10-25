using Marketgrade.Engine.Recording;
using Marketgrade.Engine.Tests.Recording;

namespace Marketgrade.Engine.Tests;

public class MarketEngineTests
{
    private static readonly EngineOptions s_fast = new()
    {
        WatchdogInterval = TimeSpan.FromMilliseconds(20),
        DeadAfter = TimeSpan.FromSeconds(5),
        ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        StatusInterval = TimeSpan.FromMilliseconds(50),
        PoolSize = 64,
    };

    [Fact]
    public async Task Stopping_the_engine_returns_every_buffer_to_the_pool()
    {
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            // Keep sending until the engine disconnects, so buffers are in flight when it stops.
            try
            {
                for (var i = 0; !c.Closed.IsCompleted; i++)
                {
                    await c.SendAsync($"{{\"n\":{i}}}");
                }
            }
            catch (Exception)
            {
            }
        });
        var engine = new MarketEngine(s_fast, [new TestAdapter(server.Uri, "A"), new TestAdapter(server.Uri, "B")]);
        engine.Start();
        await WaitUntilAsync(() => engine.Status.Exchanges.All(e => e.DataMessages > 1_000));

        await engine.StopAsync();

        Assert.Equal(engine.Pool.Created, engine.Pool.Available);
        Assert.All(engine.Status.Exchanges, e => Assert.Equal(ConnectionState.Stopped, e.State));
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Recording_engine_writes_every_message_and_returns_every_buffer()
    {
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            for (var i = 1; i <= 500; i++)
            {
                await c.SendAsync($"{{\"n\":{i}}}");
            }
            await c.Closed;
        });
        var folder = TestRecordings.CreateFolder();
        var engine = new MarketEngine(s_fast, [new TestAdapter(server.Uri, "A")], recording: RecordingSettings.Default(folder));
        engine.Start();
        await WaitUntilAsync(() => engine.Status.Exchanges[0].DataMessages == 500);

        await engine.StopAsync();

        var records = RecordingFiles.Enumerate(folder).OrderBy(f => f.Hour).SelectMany(f => TestRecordings.Read(f.Path)).ToList();
        Assert.Equal(Enumerable.Range(1, 500).Select(i => (long)i), records.Where(r => r.Type == RecordType.Message).Select(r => r.Seq));
        Assert.Equal(RecordType.ConnectionOpened, records[0].Type);
        Assert.Equal(RecordType.ConnectionClosed, records[^1].Type);
        Assert.Equal(engine.Pool.Created, engine.Pool.Available);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Status_counts_data_messages_per_second_separately_from_heartbeats()
    {
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            while (!c.Closed.IsCompleted)
            {
                await c.SendAsync("{\"data\":1}");
                await c.SendAsync("{\"heartbeat\":1}");
                await Task.Delay(5);
            }
        });
        await using var engine = new MarketEngine(s_fast, [new TestAdapter(server.Uri, "A")]);
        engine.Start();

        await WaitUntilAsync(() => engine.Status.Exchanges[0] is { State: ConnectionState.Live, MessagesPerSecond: > 0, HeartbeatMessages: > 10 });

        var status = engine.Status.Exchanges[0];
        Assert.Equal("A", status.Name);
        Assert.Equal("TST/USD", status.Pair);
        Assert.Equal(1, status.ConnectionId);
        Assert.InRange(status.MsSinceLastMessage!.Value, 0, 1_000);
        Assert.True(status.BytesPerSecond > 0);
        Assert.Contains(engine.GetRecentEvents(50), e => e.Exchange == "A" && e.Event == "Connected");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
