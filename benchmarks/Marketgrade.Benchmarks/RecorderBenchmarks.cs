using BenchmarkDotNet.Attributes;
using Marketgrade.Engine;
using Marketgrade.Engine.Recording;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Benchmarks;

/// <summary>
/// The recorder's per-message path with the default 1 MB write buffer: header, CRC, copy into the
/// buffer and a file write per full buffer, plus renting and filling the message buffer as the
/// connection does. Write throughput in MB/s is the average message size divided by the time per message.
/// </summary>
[MemoryDiagnoser]
public class RecorderBenchmarks
{
    private const int MessageCount = 100_000;

    private byte[][] _messages = [];
    private MessageBufferPool _pool = null!;
    private RecordingManager _manager = null!;
    private Recorder _recorder = null!;
    private string _folder = "";
    private long _seq;

    [Params("kraken", "coinbase", "binance")]
    public string Exchange { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        string[] files = Exchange switch
        {
            "kraken" => ["kraken/data-book-update.json", "kraken/heartbeat.json"],
            "coinbase" => ["coinbase/data-l2-update.json", "coinbase/heartbeat.json"],
            _ => ["binance/data-depth-update.json"],
        };
        _messages = [.. files.Select(Fixtures.Read)];
        _pool = new MessageBufferPool(16, 16 * 1024);
        _folder = Path.Combine(Path.GetTempPath(), "marketgrade-benchmarks", Guid.NewGuid().ToString("N"));
        var options = new EngineOptions();
        _manager = new RecordingManager(RecordingSettings.Default(_folder), options, TimeProvider.System, new ConnectionEventLog(10), NullLogger.Instance);
        _recorder = _manager.AddRecorder(Fixtures.AdapterFor($"{Exchange}/"));
        Console.WriteLine($"// Average message size: {_messages.Average(m => m.Length):0} bytes");
    }

    /// <summary>Each iteration starts a new file, so the benchmark does not fill the disk.</summary>
    [IterationCleanup]
    public void IterationCleanup()
    {
        _recorder.CloseFile();
        _manager.CompressionQueue.Reader.TryRead(out _);
        Directory.Delete(_folder, recursive: true);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Benchmark(OperationsPerInvoke = MessageCount)]
    public void WriteMessages()
    {
        _recorder.BeginBatch();
        var arrival = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;
        for (var i = 0; i < MessageCount; i++)
        {
            var message = _messages[i % _messages.Length];
            var buffer = _pool.Rent();
            message.CopyTo(buffer.Bytes, 0);
            buffer.Length = message.Length;
            buffer.ExchangeId = 1;
            buffer.ConnectionId = 1;
            buffer.Seq = ++_seq;
            buffer.ArrivalUnixNs = arrival;
            buffer.Counter = 1;
            _recorder.Process(buffer);
        }
    }
}
