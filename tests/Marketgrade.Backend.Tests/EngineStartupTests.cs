using System.Net;
using Marketgrade.Engine;
using Marketgrade.Engine.Books;
using Marketgrade.Engine.Exchanges;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Marketgrade.Backend.Tests;

/// <summary>Pair rules on startup: fetched and cached, or taken from the cache when the exchange cannot be reached.</summary>
public sealed class EngineStartupTests : IDisposable
{
    private static readonly PairRules s_kraken = new(1, "BTC/USD", 1, 8);

    // Nothing listens there: the connections fail quietly and keep retrying.
    private static readonly Uri s_noSocket = new("ws://127.0.0.1:9/");
    private static readonly Uri s_rest = new("https://rest.test/");

    private readonly TempFolder _folder = new();
    private readonly PairRulesStore _store;

    public EngineStartupTests() => _store = new PairRulesStore(new SettingsStore(Path.Combine(_folder.Path, "marketgrade.db")));

    [Fact]
    public void Store_keeps_the_latest_rules_per_pair()
    {
        Assert.Null(_store.Get(1, "BTC/USD"));

        _store.Save(s_kraken);
        _store.Save(s_kraken with { AmountDecimals = 6 });

        Assert.Equal(s_kraken with { AmountDecimals = 6 }, _store.Get(1, "BTC/USD"));
        Assert.Null(_store.Get(1, "ETH/USD"));
    }

    [Fact]
    public async Task Fetched_rules_are_cached_and_used_when_the_exchange_is_unreachable()
    {
        var body = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "pair-rules-kraken.json"));
        await using (var online = Engine(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) })))
        {
            Assert.Equal(s_kraken, await Service(online).LoadRulesAsync(online.Adapters.First(), TestContext.Current.CancellationToken));
        }

        await using var offline = Engine(Unreachable());
        Assert.Equal(s_kraken, await Service(offline).LoadRulesAsync(offline.Adapters.First(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Offline_start_uses_cached_rules_and_runs_exchanges_without_rules_with_no_book()
    {
        _store.Save(s_kraken);
        await using var engine = Engine(Unreachable());
        var service = Service(engine);

        await service.StartAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (engine.Status.Exchanges[1].BookState != BookState.NoPairRules || engine.Status.Exchanges[0].BookState != BookState.Syncing)
        {
            await Task.Delay(20, timeout.Token);
        }
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(engine.Pool.Created, engine.Pool.Available);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _folder.Dispose();
    }

    private static MarketEngine Engine(HttpMessageHandler handler) => new(
        new EngineOptions { StatusInterval = TimeSpan.FromMilliseconds(20) },
        [new KrakenAdapter(s_noSocket, s_rest), new CoinbaseAdapter(s_noSocket, s_rest)],
        httpHandler: handler);

    private EngineHostedService Service(MarketEngine engine) => new(
        engine, _store, new ConfigurationBuilder().Build(), NullLogger<EngineHostedService>.Instance);

    private static StubHandler Unreachable() => new(_ => throw new HttpRequestException("No route to host"));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
