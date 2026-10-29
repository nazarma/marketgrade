using System.Globalization;
using System.Text;
using Marketgrade.Engine;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Marketgrade.Engine.Recording;
using ZstdSharp;

// Reads recording files (.mgrec and .mgrec.zst).
//   dotnet run --project tools/Marketgrade.Reader -- cat <file> [--limit N] [--payload]
//   dotnet run --project tools/Marketgrade.Reader -- verify <file or folder>
//   dotnet run --project tools/Marketgrade.Reader -- stats <folder>
//   dotnet run --project tools/Marketgrade.Reader -- replay <file or folder>
//   dotnet run --project tools/Marketgrade.Reader -- trim <file> <out.mgrec.zst> --seconds N
//   dotnet run --project tools/Marketgrade.Reader -- delays <file or folder> --offset-ms X
const string Usage = "Usage: cat <file> [--limit N] [--payload] | verify <file or folder> | stats <folder> | replay <file or folder> "
    + "| trim <file> <out.mgrec.zst> --seconds N | delays <file or folder> --offset-ms X";

if (args.Length < 2)
{
    Console.Error.WriteLine(Usage);
    return 2;
}

switch (args[0])
{
    case "cat":
        var limit = long.MaxValue;
        var payload = false;
        for (var i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--limit":
                    limit = long.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--payload":
                    payload = true;
                    break;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return 2;
            }
        }
        return Cat(args[1], limit, payload);
    case "verify":
        return Verify(args[1]);
    case "stats":
        return Stats(args[1]);
    case "replay":
        return await ReplayAsync(args[1]);
    case "delays" when args.Length == 4 && args[2] == "--offset-ms":
        return await DelaysAsync(args[1], double.Parse(args[3], CultureInfo.InvariantCulture));
    case "trim" when args.Length == 5 && args[3] == "--seconds":
        return Trim(args[1], args[2], double.Parse(args[4], CultureInfo.InvariantCulture));
    default:
        Console.Error.WriteLine(Usage);
        return 2;
}

static int Cat(string path, long limit, bool payload)
{
    using var reader = RecordingReader.Open(path);
    if (reader.HasHeader)
    {
        var h = reader.Header;
        Console.WriteLine($"# {h.ExchangeName} (exchange {h.ExchangeId}), format {h.Version}, created {Time(h.CreatedUnixNs)}");
    }
    for (long n = 0; n < limit && reader.Read(); n++)
    {
        var r = reader.Current;
        var line = $"{r.Type,-16} seq {r.Seq,-10} {Time(r.ArrivalUnixNs)}  conn {r.ConnectionId,-4} {r.PayloadLength,8} B";
        if (r.Type == RecordType.Gap)
        {
            var (first, last, count) = RecordFormat.ReadGapPayload(reader.Payload);
            line += $"  seq {first} to {last}, {count} not recorded";
        }
        else if (payload || r.Type is not (RecordType.Message or RecordType.Snapshot))
        {
            line += "  " + Encoding.UTF8.GetString(reader.Payload);
        }
        Console.WriteLine(line);
    }
    if (reader.Error is not null)
    {
        Console.Error.WriteLine($"{path}: {reader.Error}");
        return 1;
    }
    return 0;
}

static int Verify(string target)
{
    string[] files = File.Exists(target)
        ? [target]
        : [.. Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(RecordingFiles.Extension, StringComparison.Ordinal)
                || f.EndsWith(RecordingFiles.CompressedExtension, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    var broken = 0;
    foreach (var file in files)
    {
        var (records, _, error) = Scan(file);
        if (error is null)
        {
            Console.WriteLine($"OK      {file} ({records} records)");
        }
        else
        {
            broken++;
            Console.WriteLine($"BROKEN  {file}: {error} (after {records} valid records)");
        }
    }
    Console.WriteLine($"{files.Length} files, {broken} broken");
    return broken == 0 ? 0 : 1;
}

static int Stats(string folder)
{
    var errors = 0;
    foreach (var exchange in RecordingFiles.Enumerate(folder).GroupBy(f => f.Exchange).OrderBy(g => g.Key))
    {
        var byType = new Dictionary<RecordType, long>();
        long payloadBytes = 0, gaps = 0, dropped = 0, compressedFiles = 0, compressedDisk = 0, compressedRaw = 0;
        foreach (var file in exchange)
        {
            using var reader = RecordingReader.Open(file.Path);
            while (reader.Read())
            {
                var r = reader.Current;
                byType[r.Type] = byType.GetValueOrDefault(r.Type) + 1;
                payloadBytes += r.PayloadLength;
                if (r.Type == RecordType.Gap)
                {
                    gaps++;
                    dropped += RecordFormat.ReadGapPayload(reader.Payload).Count;
                }
            }
            if (reader.Error is not null)
            {
                errors++;
                Console.Error.WriteLine($"{file.Path}: {reader.Error}");
            }
            if (file.Compressed)
            {
                compressedFiles++;
                compressedDisk += file.Length;
                compressedRaw += reader.ValidLength;
            }
        }

        var disk = exchange.Sum(f => f.Length);
        var messages = byType.GetValueOrDefault(RecordType.Message);
        Console.WriteLine($"{exchange.Key}");
        Console.WriteLine($"  files      {exchange.Count()} ({compressedFiles} compressed)");
        Console.WriteLine($"  records    {messages} messages, {byType.GetValueOrDefault(RecordType.ConnectionOpened)} opened, " +
            $"{byType.GetValueOrDefault(RecordType.ConnectionClosed)} closed, {gaps} gaps, " +
            $"{byType.GetValueOrDefault(RecordType.Snapshot)} snapshots, {byType.GetValueOrDefault(RecordType.BookReset)} book resets");
        Console.WriteLine($"  dropped    {dropped} messages not recorded");
        Console.WriteLine($"  bytes      {payloadBytes:N0} payload, {disk:N0} on disk");
        if (messages > 0)
        {
            Console.WriteLine($"  per msg    {payloadBytes / (double)messages:0} B payload, {disk / (double)messages:0.0} B on disk");
        }
        if (compressedFiles > 0)
        {
            Console.WriteLine($"  ratio      {compressedRaw / (double)compressedDisk:0.0} : 1 ({compressedRaw:N0} to {compressedDisk:N0} bytes)");
        }
    }
    return errors == 0 ? 0 : 1;
}

// Rebuilds each exchange's book from its recordings and reports the checks; exits with 1 on a failure
// the live book did not have too (no BookReset record right after it).
static async Task<int> ReplayAsync(string target)
{
    var files = File.Exists(target)
        ? RecordingFiles.TryParse(Path.GetFullPath(target), out var single) ? [single] : throw new ArgumentException($"{target} is not in the recordings folder layout")
        : RecordingFiles.Enumerate(target);
    var unexplained = 0;
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    foreach (var exchange in files.GroupBy(f => f.Exchange).OrderBy(g => g.Key))
    {
        var adapter = ExchangeAdapters.CreateDefault().FirstOrDefault(a => a.Name.Equals(exchange.Key, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            Console.WriteLine($"{exchange.Key}: unknown exchange, skipped");
            continue;
        }
        var rules = await RulesAsync(http, adapter);
        var replay = new BookReplay(adapter, rules);
        foreach (var file in exchange.OrderBy(f => f.Hour).ThenBy(f => f.Part))
        {
            using var reader = RecordingReader.Open(file.Path);
            replay.Read(reader);
            if (reader.Error is not null)
            {
                Console.Error.WriteLine($"{file.Path}: {reader.Error}");
            }
        }
        replay.Finish();
        unexplained += replay.UnexplainedFailures;

        var failures = replay.Failures;
        Console.WriteLine($"{adapter.Name} ({rules.PriceDecimals} price and {rules.AmountDecimals} amount decimals)");
        Console.WriteLine($"  time       {Time(replay.FirstUnixNs)} to {Time(replay.LastUnixNs)}");
        Console.WriteLine($"  messages   {replay.Messages} replayed, {replay.Applied} applied, {replay.Syncs} snapshots applied and checked");
        Console.WriteLine($"  failures   {failures.Count(f => f.Kind == FailureKind.Checksum)} checksum, {failures.Count(f => f.Kind == FailureKind.Sequence)} sequence, "
            + $"{failures.Count(f => f.Kind == FailureKind.Parse)} parse; {replay.UnexplainedFailures} without a live book reset");
        Console.WriteLine($"  trades     {replay.Trades} read, {replay.TradeFailures} trade messages unreadable");
        Console.WriteLine($"  live       {replay.LiveResets.Count} book resets, {replay.RecordingGaps} recording gaps");
        Console.WriteLine($"  levels     at most {replay.Book.Bids.MaxCount} bids and {replay.Book.Asks.MaxCount} asks");
        foreach (var failure in failures)
        {
            Console.WriteLine($"  {(failure.Explained ? "failure  " : "UNEXPLAINED")} {Time(failure.ArrivalUnixNs)} seq {failure.Seq} conn {failure.ConnectionId}: {failure.Cause}");
        }
        foreach (var reset in replay.LiveResets)
        {
            Console.WriteLine($"  live reset {Time(reset.ArrivalUnixNs)} seq {reset.Seq} conn {reset.ConnectionId}: {reset.Reason}");
        }
        foreach (var missing in replay.MissingSnapshots)
        {
            Console.WriteLine($"  missing snapshot {Time(missing.FromUnixNs)} to {Time(missing.ToUnixNs)}: {missing.Events} events not replayed");
        }
    }
    return unexplained == 0 ? 0 : 1;
}

// The signed delay of every message with an exchange timestamp (arrival minus that timestamp, corrected by
// the local clock's offset from NTP, which the status shows as clockOffsetMs), negatives included.
static async Task<int> DelaysAsync(string target, double offsetMs)
{
    var files = File.Exists(target)
        ? RecordingFiles.TryParse(Path.GetFullPath(target), out var single) ? [single] : throw new ArgumentException($"{target} is not in the recordings folder layout")
        : RecordingFiles.Enumerate(target);
    var offsetNs = (long)(offsetMs * 1e6);
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    foreach (var exchange in files.GroupBy(f => f.Exchange).OrderBy(g => g.Key))
    {
        var adapter = ExchangeAdapters.CreateDefault().FirstOrDefault(a => a.Name.Equals(exchange.Key, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            continue;
        }
        var delays = new List<(long Arrival, double Ms)>();
        var replay = new BookReplay(adapter, await RulesAsync(http, adapter))
        {
            ExchangeTime = (arrival, exchangeTime) => delays.Add((arrival, (arrival - exchangeTime - offsetNs) / 1e6)),
        };
        foreach (var file in exchange.OrderBy(f => f.Hour).ThenBy(f => f.Part))
        {
            using var reader = RecordingReader.Open(file.Path);
            replay.Read(reader);
        }
        if (delays.Count == 0)
        {
            continue;
        }
        var sorted = delays.Select(d => d.Ms).Order().ToArray();
        double At(double percentile) => sorted[Math.Clamp((int)Math.Ceiling(percentile / 100 * sorted.Length) - 1, 0, sorted.Length - 1)];
        var negative = sorted.Count(d => d < 0);
        Console.WriteLine($"{adapter.Name}: {sorted.Length} messages, {Time(delays[0].Arrival)} to {Time(delays[^1].Arrival)}");
        Console.WriteLine($"  negative   {negative} ({100.0 * negative / sorted.Length:0.0} %)");
        Console.WriteLine("  signed ms  " + string.Join("  ", new[] { 0.1, 1, 5, 10, 25, 50, 75, 90, 99, 99.9 }
            .Select(p => $"p{p.ToString(CultureInfo.InvariantCulture)} {At(p):0.0}")) + $"  min {sorted[0]:0.0}  max {sorted[^1]:0.0}");
        if (negative > 0)
        {
            var ahead = sorted.Take(negative).Select(d => -d).Order().ToArray();
            double Ahead(double percentile) => ahead[Math.Clamp((int)Math.Ceiling(percentile / 100 * ahead.Length) - 1, 0, ahead.Length - 1)];
            Console.WriteLine($"  ahead ms   (negative ones) p50 {Ahead(50):0.0}  p90 {Ahead(90):0.0}  p99 {Ahead(99):0.0}  max {ahead[^1]:0.0}");
        }
        Console.WriteLine("  per minute negative %: " + string.Join(" ", delays
            .GroupBy(d => d.Arrival / 60_000_000_000)
            .Select(g => $"{100.0 * g.Count(d => d.Ms < 0) / g.Count():0}")));
    }
    return 0;
}

// The rules the app uses, from the exchange; offline, the values last seen when this tool was written.
static async Task<PairRules> RulesAsync(HttpClient http, ExchangeAdapter adapter)
{
    try
    {
        return await PairRulesClient.FetchAsync(http, adapter, CancellationToken.None);
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException)
    {
        var known = adapter.Id switch
        {
            1 => new PairRules(1, adapter.Pair, 1, 8),
            2 => new PairRules(2, adapter.Pair, 2, 8),
            _ => new PairRules(3, adapter.Pair, 2, 5),
        };
        Console.Error.WriteLine($"{adapter.Name}: pair rules could not be fetched ({e.Message}), using {known.PriceDecimals}/{known.AmountDecimals}");
        return known;
    }
}

// Copies the first N seconds of a recording into a new file compressed at zstd level 19, for test fixtures.
static int Trim(string path, string output, double seconds)
{
    using var reader = RecordingReader.Open(path);
    if (!reader.HasHeader)
    {
        Console.Error.WriteLine($"{path}: {reader.Error}");
        return 1;
    }
    using var raw = new MemoryStream();
    var header = new byte[RecordFormat.FileHeaderSize(reader.Header.ExchangeName)];
    RecordFormat.WriteFileHeader(header, reader.Header.ExchangeId, reader.Header.ExchangeName, reader.Header.CreatedUnixNs);
    raw.Write(header);
    long first = 0, records = 0;
    var recordHeader = new byte[RecordFormat.RecordHeaderSize];
    while (reader.Read())
    {
        var r = reader.Current;
        first = first == 0 ? r.ArrivalUnixNs : first;
        if (r.ArrivalUnixNs - first > seconds * 1e9)
        {
            break;
        }
        RecordFormat.WriteRecordHeader(recordHeader, r.Type, reader.Payload, r.Seq, r.ArrivalUnixNs, r.ConnectionId);
        raw.Write(recordHeader);
        raw.Write(reader.Payload);
        records++;
    }
    using (var file = File.Create(output))
    using (var compressor = new CompressionStream(file, level: 19))
    {
        raw.Position = 0;
        raw.CopyTo(compressor);
    }
    Console.WriteLine($"{output}: {records} records, {raw.Length:N0} bytes, {new FileInfo(output).Length:N0} compressed");
    return 0;
}

static (long Records, long Length, string? Error) Scan(string path)
{
    try
    {
        using var reader = RecordingReader.Open(path);
        long records = 0;
        while (reader.Read())
        {
            records++;
        }
        return (records, reader.ValidLength, reader.Error);
    }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ZstdSharp.ZstdException)
    {
        return (0, 0, e.Message);
    }
}

static string Time(long unixNs) =>
    DateTime.UnixEpoch.AddTicks(unixNs / 100).ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture)
    + (unixNs % 100).ToString("00", CultureInfo.InvariantCulture) + "Z";
