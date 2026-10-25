using Marketgrade.Engine;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Recording;
using Marketgrade.Probe;
using Microsoft.Extensions.Logging;

// Runs the engine against the real exchanges and prints what it receives.
//   dotnet run --project tools/Marketgrade.Probe -- [--seconds 60] [--interval 5] [--record <dir>]
//   dotnet run --project tools/Marketgrade.Probe -- --capture <dir> [--seconds 15]
int? secondsArgument = null;
var interval = 5;
string? captureDirectory = null;
string? recordDirectory = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seconds":
            secondsArgument = int.Parse(args[++i]);
            break;
        case "--interval":
            interval = int.Parse(args[++i]);
            break;
        case "--capture":
            captureDirectory = args[++i];
            break;
        case "--record":
            recordDirectory = Path.GetFullPath(args[++i]);
            break;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

var adapters = ExchangeAdapters.CreateDefault();
if (captureDirectory is not null)
{
    await SampleCapture.RunAsync(adapters, captureDirectory, TimeSpan.FromSeconds(secondsArgument ?? 15));
    return 0;
}

var seconds = secondsArgument ?? 60;

using var loggerFactory = LoggerFactory.Create(builder => builder
    .AddSimpleConsole(options => options.TimestampFormat = "HH:mm:ss ")
    .SetMinimumLevel(LogLevel.Warning));
// Records with the default settings, so the files match what the app writes.
await using var engine = new MarketEngine(new EngineOptions(), adapters, loggerFactory.CreateLogger("Engine"),
    recordDirectory is null ? null : RecordingSettings.Default(recordDirectory));

Console.WriteLine($"Probing {string.Join(", ", adapters.Select(a => $"{a.Name} {a.Pair}"))} for {seconds} s");
if (recordDirectory is not null)
{
    Console.WriteLine($"Recording to {recordDirectory}");
}
var rules = new List<PairRules>();
foreach (var adapter in adapters)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        rules.Add(await engine.FetchPairRulesAsync(adapter, timeout.Token));
    }
    catch (Exception e) when (e is HttpRequestException or OperationCanceledException or FormatException or System.Text.Json.JsonException)
    {
        Console.WriteLine($"{adapter.Name}: no pair rules, no book ({e.Message})");
    }
}
engine.Start(rules);
var started = DateTime.UtcNow;
var printed = 0;
for (var elapsed = interval; elapsed <= seconds; elapsed += interval)
{
    await Task.Delay(started.AddSeconds(elapsed) - DateTime.UtcNow);
    Console.WriteLine();
    Console.WriteLine($"After {elapsed} s");
    StatusTable.Print(engine.Status);
    var events = engine.GetRecentEvents(200);
    foreach (var e in events.Skip(printed))
    {
        Console.WriteLine($"  {e.TimeUtc:HH:mm:ss.fff} {e.Exchange,-9} {e.Event}{(e.Detail is null ? "" : $" ({e.Detail})")}");
    }
    printed = events.Count;
}

var running = engine.Status;
await engine.StopAsync();

Console.WriteLine();
Console.WriteLine($"Summary after {seconds} s");
StatusTable.PrintSummary(running, seconds);
Console.WriteLine($"Buffer pool: {engine.Pool.Available} of {engine.Pool.Created} free, {engine.Pool.Misses} misses");
if (running.Recording is { } recording)
{
    Console.WriteLine($"Recording: {recording.State}, {RecordingFiles.TotalBytes(recordDirectory!) / 1024.0 / 1024.0:0.00} MB on disk, " +
        $"{recording.DroppedMessages} dropped{(recording.LastError is null ? "" : $", last error: {recording.LastError}")}");
}
return 0;
