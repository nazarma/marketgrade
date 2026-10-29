using System.Net;
using System.Text.Json;
using Marketgrade.Backend.History;
using Marketgrade.Engine.History;
using Microsoft.Extensions.DependencyInjection;

namespace Marketgrade.Backend.Tests;

/// <summary>The history endpoints against the backend's own DuckDB file in a temporary data folder.</summary>
public sealed class HistoryEndpointTests : IAsyncLifetime
{
    private readonly BackendFactory _factory = new();
    private readonly DateTime _thisMinute;

    public HistoryEndpointTests()
    {
        var now = DateTime.UtcNow;
        _thisMinute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
    }

    private HistoryStore Store => _factory.Services.GetRequiredService<HistoryStore>();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task<JsonElement> GetAsync(string path)
    {
        var response = await _factory.CreateAuthorizedClient().GetAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return json.RootElement.Clone();
    }

    private static JsonElement Exchange(JsonElement root, string name) =>
        root.GetProperty("exchanges").EnumerateArray().Single(e => e.GetProperty("exchange").GetString() == name);

    [Fact]
    public async Task Delay_lists_every_exchange_with_its_minutes_oldest_first()
    {
        Store.Add([HistoryTestData.Minute(1, _thisMinute.AddMinutes(-3), delayMs: 31), HistoryTestData.Minute(2, _thisMinute.AddMinutes(-3), delayMs: 22)]);
        Store.Add([HistoryTestData.Minute(1, _thisMinute.AddMinutes(-1), delayMs: 35)]);
        // The app restarted within the minute: the row with more delays measured counts.
        Store.Add([HistoryTestData.Minute(1, _thisMinute.AddMinutes(-1), delayMs: 90, delays: 5)]);
        Store.Add([HistoryTestData.Minute(1, _thisMinute.AddMinutes(-90), delayMs: 500)]);

        var root = await GetAsync("/history/delay?minutes=60");

        var kraken = Exchange(root, "Kraken").GetProperty("points").EnumerateArray().ToList();
        Assert.Equal([31.0, 35.0], kraken.Select(p => p.GetProperty("p50Ms").GetDouble()));
        Assert.Equal(_thisMinute.AddMinutes(-3), kraken[0].GetProperty("minuteUtc").GetDateTime().ToUniversalTime());
        Assert.Single(Exchange(root, "Coinbase").GetProperty("points").EnumerateArray());
        Assert.Empty(Exchange(root, "Binance").GetProperty("points").EnumerateArray());
    }

    [Fact]
    public async Task Health_has_uptime_over_the_running_time_approximate_percentiles_and_the_minutes()
    {
        // Kraken ran 3 minutes: one with a 6 s gap and a reconnect. Then the app did not run for 2 minutes.
        Store.Add([HistoryTestData.Minute(1, _thisMinute.AddMinutes(-5), delayMs: 30)]);
        Store.Add([HistoryTestData.Minute(1, _thisMinute.AddMinutes(-4), valid: 54, invalid: 6, reconnects: 1, gaps: 1, delayMs: 30, maxMs: 412)]);
        Store.Add([HistoryTestData.Minute(1, _thisMinute.AddMinutes(-3), delayMs: 200, delays: 10)]);
        Store.Add(new ReconnectRecord(1, "Kraken", _thisMinute.AddMinutes(-4).AddSeconds(7), "Closed by server: 1006"));

        var root = await GetAsync("/history/health?hours=24");

        var kraken = Exchange(root, "Kraken");
        Assert.Equal(174.0 / 180.0, kraken.GetProperty("uptime").GetDouble(), 9);
        Assert.Equal(180, kraken.GetProperty("runningSeconds").GetDouble());
        Assert.InRange(kraken.GetProperty("delayP50Ms").GetDouble(), 30 * 0.88, 30 * 1.13);
        Assert.InRange(kraken.GetProperty("delayP99Ms").GetDouble(), 200 * 0.88, 200 * 1.13);
        Assert.Equal(600, kraken.GetProperty("delayMaxMs").GetDouble());
        Assert.Equal(3_000, kraken.GetProperty("bookUpdates").GetInt64());
        Assert.Equal(60, kraken.GetProperty("trades").GetInt64());
        var minutes = kraken.GetProperty("minutes");
        Assert.Equal(3, minutes.GetProperty("minuteMs").GetArrayLength());
        Assert.Equal([60.0, 54.0, 60.0], minutes.GetProperty("valid").EnumerateArray().Select(v => v.GetDouble()));
        Assert.Equal([0, 1, 0], minutes.GetProperty("reconnects").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal([0, 1, 0], minutes.GetProperty("gaps").EnumerateArray().Select(v => v.GetInt32()));
        Assert.Equal("Closed by server: 1006", Assert.Single(kraken.GetProperty("reconnects").EnumerateArray()).GetProperty("reason").GetString());

        var binance = Exchange(root, "Binance");
        Assert.Equal(JsonValueKind.Null, binance.GetProperty("uptime").ValueKind);
        Assert.Equal(JsonValueKind.Null, binance.GetProperty("delayP50Ms").ValueKind);
        Assert.Equal(0, binance.GetProperty("minutes").GetProperty("minuteMs").GetArrayLength());
    }

    [Fact]
    public async Task Minutes_with_more_than_10_percent_negative_delays_are_a_lower_bound()
    {
        Store.Add([HistoryTestData.Minute(1, _thisMinute.AddMinutes(-3), negative: 11, aheadMs: 6)]);
        Store.Add([HistoryTestData.Minute(1, _thisMinute.AddMinutes(-2), negative: 10, aheadMs: 2)]);
        Store.Add([HistoryTestData.Minute(1, _thisMinute.AddMinutes(-1))]);
        Store.Add([HistoryTestData.Minute(2, _thisMinute.AddMinutes(-1), negative: 40, aheadMs: 12)]);

        var delay = await GetAsync("/history/delay?minutes=60");
        var points = Exchange(delay, "Kraken").GetProperty("points").EnumerateArray().ToList();
        Assert.Equal([true, false, false], points.Select(p => p.GetProperty("lowerBound").GetBoolean()));
        Assert.Equal(6, points[0].GetProperty("aheadMs").GetDouble());
        Assert.Equal(JsonValueKind.Null, points[2].GetProperty("aheadMs").ValueKind);

        var health = await GetAsync("/history/health?hours=24");
        // 21 of 300 delays: not a lower bound over the range; how far ahead, averaged by count.
        var kraken = Exchange(health, "Kraken");
        Assert.False(kraken.GetProperty("delayLowerBound").GetBoolean());
        Assert.Equal((11 * 6 + 10 * 2) / 21.0, kraken.GetProperty("delayAheadMs").GetDouble(), 9);
        var coinbase = Exchange(health, "Coinbase");
        Assert.True(coinbase.GetProperty("delayLowerBound").GetBoolean());
        Assert.Equal(12, coinbase.GetProperty("delayAheadMs").GetDouble());
        Assert.Equal(JsonValueKind.Null, Exchange(health, "Binance").GetProperty("delayAheadMs").ValueKind);
    }

    [Fact]
    public async Task Gaps_in_the_range_newest_first()
    {
        Store.Add(new GapRecord(1, "Kraken", _thisMinute.AddHours(-2), _thisMinute.AddHours(-2).AddSeconds(0.8), GapKind.Checksum, "Checksum mismatch: Kraken sent 1, book has 2", "Reconnected and rebuilt the book from a new snapshot"));
        Store.Add(new GapRecord(2, "Coinbase", _thisMinute.AddHours(-1), _thisMinute.AddHours(-1).AddSeconds(2.1), GapKind.ConnectionLost, "Connection lost: Closed by server", "Reconnected and rebuilt the book from a new snapshot"));
        Store.Add(new GapRecord(3, "Binance", _thisMinute.AddHours(-30), _thisMinute.AddHours(-30), GapKind.Sequence, "old", "x"));

        var root = await GetAsync("/history/gaps?hours=24");

        var gaps = root.EnumerateArray().ToList();
        Assert.Equal(["Coinbase", "Kraken"], gaps.Select(g => g.GetProperty("exchange").GetString()));
        Assert.Equal(0.8, gaps[1].GetProperty("durationSeconds").GetDouble(), 6);
        Assert.Equal("Checksum", gaps[1].GetProperty("kind").GetString());
        Assert.Equal("ConnectionLost", gaps[0].GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("/history/delay?minutes=0")]
    [InlineData("/history/delay?minutes=1441")]
    [InlineData("/history/health?hours=169")]
    [InlineData("/history/gaps?hours=0")]
    public async Task Ranges_outside_the_limits_are_rejected(string path)
    {
        var response = await _factory.CreateAuthorizedClient().GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
