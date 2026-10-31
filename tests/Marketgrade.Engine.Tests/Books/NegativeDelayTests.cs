using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Engine.Tests.Books;

/// <summary>Delays below zero count as zero in the percentiles and are counted apart, with how far below zero they went.</summary>
public sealed class NegativeDelayTests : IDisposable
{
    private const long Ms = 1_000_000;

    private readonly string[] _lines = TestMessages.Sequence("kraken-book.jsonl");
    private readonly MessageBufferPool _pool = new(8, 16 * 1024);
    private readonly HttpClient _http = new();
    private readonly BookWorker _worker;

    public NegativeDelayTests()
    {
        var adapter = new KrakenAdapter();
        var options = new EngineOptions();
        var events = new ConnectionEventLog(50);
        var queue = Channel.CreateUnbounded<MessageBuffer>();
        var connection = new ExchangeConnection(adapter, _pool, [queue.Writer], options, events);
        _worker = new BookWorker(adapter, connection, queue.Reader, new ManualResetEventSlim(), _pool, _http, new NtpClock(),
            options, events, NullLogger.Instance);
        _worker.Prepare(new PairRules(1, "BTC/USD", 1, 8));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(2, false)]
    public void Negative_delays_are_counted_and_make_a_lower_bound_above_10_percent(int negative, bool lowerBound)
    {
        // The snapshot arrives well before its own timestamp, so the messages after it are not catch-up.
        Process(_lines[2], Timestamp(_lines[2]) - 10_000 * Ms);
        for (var i = 0; i < 20; i++)
        {
            var line = _lines[3 + i];
            Process(line, Timestamp(line) + (i < negative ? -4 * Ms : 20 * Ms));
        }
        _worker.PublishStatistics(Stopwatch.GetTimestamp());

        Assert.Equal(20, _worker.DelayCount);
        Assert.Equal(negative, _worker.DelayNegative);
        Assert.Equal(4 * Ms, _worker.DelayAheadP99Ns, 4 * Ms * 0.02);
        Assert.Equal(lowerBound, NegativeDelays.IsLowerBound(_worker.DelayNegative, _worker.DelayCount));
    }

    [Fact]
    public void No_negative_delays_no_lead()
    {
        Process(_lines[2], Timestamp(_lines[2]) - 10_000 * Ms);
        Process(_lines[3], Timestamp(_lines[3]) + 20 * Ms);
        _worker.PublishStatistics(Stopwatch.GetTimestamp());

        Assert.Equal(1, _worker.DelayCount);
        Assert.Equal(0, _worker.DelayNegative);
        Assert.Equal(0, _worker.DelayAheadP99Ns);
        Assert.False(NegativeDelays.IsLowerBound(0, 0));
    }

    public void Dispose() => _http.Dispose();

    private void Process(string message, long arrivalUnixNs)
    {
        _worker.Process(TestMessages.Buffer(_pool, message, connectionId: 1, arrivalUnixNs: arrivalUnixNs));
    }

    private static long Timestamp(string message)
    {
        using var json = JsonDocument.Parse(message);
        var text = json.RootElement.GetProperty("data")[0].GetProperty("timestamp").GetString()!;
        Assert.True(Iso8601.TryParseUnixNs(System.Text.Encoding.ASCII.GetBytes(text), out var unixNs));
        return unixNs;
    }
}
