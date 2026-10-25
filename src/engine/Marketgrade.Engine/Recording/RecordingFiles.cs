using System.Globalization;
using System.Text.RegularExpressions;

namespace Marketgrade.Engine.Recording;

/// <param name="Hour">Hours since the Unix epoch (UTC).</param>
/// <param name="Part">1 for <c>HH.mgrec</c>, n for <c>HH-n.mgrec</c>.</param>
public sealed record RecordingFile(string Path, string Exchange, long Hour, int Part, bool Compressed, long Length);

/// <summary>
/// The folder layout <c>&lt;folder&gt;/&lt;exchange&gt;/&lt;yyyy-MM-dd&gt;/&lt;HH&gt;.mgrec[.zst]</c>. Only files that
/// match it exactly count as recordings; everything else in the folder is never touched.
/// </summary>
public static partial class RecordingFiles
{
    public const string Extension = ".mgrec";

    public const string CompressedExtension = ".mgrec.zst";

    public const string TempExtension = ".mgrec.zst.tmp";

    public const long NsPerHour = 3_600_000_000_000;

    public static string ExchangeFolder(string folder, string exchange) => Path.Combine(folder, exchange.ToLowerInvariant());

    public static string PathFor(string folder, string exchange, long hour, int part = 1, bool compressed = false)
    {
        var start = HourStart(hour);
        var name = part == 1 ? start.ToString("HH", CultureInfo.InvariantCulture) : $"{start:HH}-{part}";
        return Path.Combine(
            ExchangeFolder(folder, exchange),
            start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            name + (compressed ? CompressedExtension : Extension));
    }

    public static DateTime HourStart(long hour) => DateTime.UnixEpoch.AddHours(hour);

    public static long HourOf(DateTimeOffset time) => (time.UtcTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerHour;

    /// <summary>Parses a path in the folder layout; false for anything else.</summary>
    public static bool TryParse(string path, out RecordingFile file)
    {
        file = null!;
        var match = FileName().Match(Path.GetFileName(path));
        var dateFolder = Path.GetDirectoryName(path);
        var exchangeFolder = dateFolder is null ? null : Path.GetDirectoryName(dateFolder);
        if (!match.Success || dateFolder is null || exchangeFolder is null)
        {
            return false;
        }
        var exchange = Path.GetFileName(exchangeFolder);
        if (!ExchangeName().IsMatch(exchange)
            || !DateTime.TryParseExact(Path.GetFileName(dateFolder), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date))
        {
            return false;
        }
        var hourOfDay = int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
        var part = match.Groups[2].Success ? int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture) : 1;
        if (hourOfDay > 23 || part < 1)
        {
            return false;
        }
        file = new RecordingFile(path, exchange, HourOf(date) + hourOfDay, part, match.Groups[3].Success, 0);
        return true;
    }

    /// <summary>All recordings of all exchanges under <paramref name="folder"/>, with their lengths.</summary>
    public static List<RecordingFile> Enumerate(string folder)
    {
        var files = new List<RecordingFile>();
        if (!Directory.Exists(folder))
        {
            return files;
        }
        foreach (var exchangeFolder in Directory.EnumerateDirectories(folder))
        {
            files.AddRange(EnumerateExchange(exchangeFolder));
        }
        return files;
    }

    /// <summary>The recordings in one exchange folder, with their lengths.</summary>
    public static List<RecordingFile> EnumerateExchange(string exchangeFolder)
    {
        var files = new List<RecordingFile>();
        if (!Directory.Exists(exchangeFolder) || !ExchangeName().IsMatch(Path.GetFileName(exchangeFolder)))
        {
            return files;
        }
        foreach (var dateFolder in Directory.EnumerateDirectories(exchangeFolder))
        {
            foreach (var info in new DirectoryInfo(dateFolder).EnumerateFiles())
            {
                if (TryParse(info.FullName, out var file))
                {
                    files.Add(file with { Length = info.Length });
                }
            }
        }
        return files;
    }

    public static long TotalBytes(string folder) => Enumerate(folder).Sum(f => f.Length);

    /// <summary>True for a leftover of an interrupted compression in the folder layout.</summary>
    public static bool IsTempFile(string path) =>
        path.EndsWith(TempExtension, StringComparison.Ordinal) && TryParse(path[..^".tmp".Length], out _);

    [GeneratedRegex(@"^(\d{2})(?:-(\d+))?\.mgrec(\.zst)?$")]
    private static partial Regex FileName();

    [GeneratedRegex("^[a-z0-9]+$")]
    private static partial Regex ExchangeName();
}
