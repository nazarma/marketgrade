using System.Text;
using System.Threading.Channels;
using Marketgrade.Engine.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine.Tests.Recording;

public class RecorderQueueTests
{
    private static readonly EngineOptions s_options = new()
    {
        WatchdogInterval = TimeSpan.FromMilliseconds(20),
        DeadAfter = TimeSpan.FromSeconds(10),
        ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        PoolSize = 32,
    };

    [Fact]
    public async Task Full_recorder_queue_drops_for_the_recorder_only_and_records_the_gap()
    {
        var sendLast = new TaskCompletionSource();
        await using var server = await FakeExchangeServer.StartAsync(async c =>
        {
            for (var i = 1; i <= 10; i++)
            {
                await c.SendAsync($"message {i}");
            }
            await sendLast.Task;
            await c.SendAsync("message 11");
            await c.Closed;
        });

        var pool = new MessageBufferPool(s_options.PoolSize, s_options.BufferSize);
        var stats = Channel.CreateUnbounded<MessageBuffer>();
        // Room for four records and nobody reading yet: ConnectionOpened and messages 1 to 3 fit.
        var recorderQueue = Channel.CreateBounded<MessageBuffer>(4);
        using var metrics = new EngineMetrics();
        var events = new ConnectionEventLog(100);
        var connection = new ExchangeConnection(
            new TestAdapter(server.Uri), pool, [stats.Writer], s_options, events, metrics, recorder: recorderQueue.Writer);
        using var tempFolder = new TempFolder();
        var folder = tempFolder.Path;
        var manager = new RecordingManager(RecordingSettings.Default(folder), s_options, TimeProvider.System, events, NullLogger.Instance);
        var recorder = manager.AddRecorder(connection.Adapter);
        connection.Start();

        var received = new List<string>();
        for (var i = 0; i < 10; i++)
        {
            received.Add(await ReadAndReleaseAsync(stats.Reader));
        }
        Assert.Equal(7, connection.RecordingDropped);

        var recording = Task.Run(() => recorder.RunAsync(recorderQueue.Reader), TestContext.Current.CancellationToken);
        await RecorderHarness.WaitUntilAsync(() => recorderQueue.Reader.Count == 0);
        sendLast.SetResult();
        received.Add(await ReadAndReleaseAsync(stats.Reader));
        await connection.StopAsync();
        await recording;
        while (stats.Reader.TryRead(out var extra))
        {
            extra.Release();
        }

        Assert.Equal(Enumerable.Range(1, 11).Select(i => $"message {i}"), received);
        var records = RecordingFiles.Enumerate(folder).OrderBy(f => f.Hour).SelectMany(f => TestRecordings.Read(f.Path)).ToList();
        Assert.Equal(
            [RecordType.ConnectionOpened, RecordType.Message, RecordType.Message, RecordType.Message, RecordType.Gap, RecordType.Message, RecordType.ConnectionClosed],
            records.Select(r => r.Type));
        Assert.Equal(server.Uri.AbsoluteUri, Encoding.UTF8.GetString(records[0].Payload));
        Assert.Equal([1, 2, 3], records.Skip(1).Take(3).Select(r => r.Seq));
        Assert.Equal((4, 10, 7), RecordFormat.ReadGapPayload(records[4].Payload));
        Assert.Equal("message 11", Encoding.UTF8.GetString(records[5].Payload));
        Assert.Equal(11, records[6].Seq);
        Assert.Equal("Stopped", Encoding.UTF8.GetString(records[6].Payload));
        Assert.Equal(pool.Created, pool.Available);
    }

    private static async Task<string> ReadAndReleaseAsync(ChannelReader<MessageBuffer> reader)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = await reader.ReadAsync(timeout.Token);
        Assert.Equal(RecordType.Message, buffer.Type);
        var text = Encoding.UTF8.GetString(buffer.Span);
        buffer.Release();
        return text;
    }
}
