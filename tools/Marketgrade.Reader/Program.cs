using System.Globalization;
using System.Text;
using Marketgrade.Engine.Recording;

// Reads recording files (.mgrec and .mgrec.zst).
//   dotnet run --project tools/Marketgrade.Reader -- cat <file> [--limit N] [--payload]
//   dotnet run --project tools/Marketgrade.Reader -- verify <file or folder>
//   dotnet run --project tools/Marketgrade.Reader -- stats <folder>
const string Usage = "Usage: cat <file> [--limit N] [--payload] | verify <file or folder> | stats <folder>";

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
        else if (payload || r.Type != RecordType.Message)
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
            $"{byType.GetValueOrDefault(RecordType.ConnectionClosed)} closed, {gaps} gaps");
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
