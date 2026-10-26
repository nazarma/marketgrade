using System.Text.Json;
using Marketgrade.Engine;
using Marketgrade.Engine.Books;

namespace Marketgrade.Backend;

/// <summary>
/// Starts the exchange connections with the backend and stops them on shutdown, after which every
/// message buffer is back in the pool. Each exchange's pair rules are fetched first, in the background so
/// the backend is ready at once; when an exchange cannot be reached its cached rules are used, and without
/// either it keeps no book. MARKETGRADE_ENGINE_DISABLED=1 keeps the engine stopped (tests).
/// </summary>
public sealed class EngineHostedService(
    MarketEngine engine,
    PairRulesStore pairRules,
    IConfiguration configuration,
    ILogger<EngineHostedService> logger) : IHostedService
{
    private static readonly TimeSpan s_fetchTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _stopping = new();
    private Task? _starting;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration["MARKETGRADE_ENGINE_DISABLED"] == "1")
        {
            logger.LogInformation("Engine is disabled, not connecting to exchanges");
            return Task.CompletedTask;
        }

        _starting = Task.Run(() => StartEngineAsync(_stopping.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (_starting is not null)
        {
            await _starting;
        }
        await engine.StopAsync();
        logger.LogInformation("Engine stopped, {Available} of {Created} buffers in the pool", engine.Pool.Available, engine.Pool.Created);
    }

    /// <summary>Fresh rules from the exchange (saved for next time), else the cached ones, else null.</summary>
    public async Task<PairRules?> LoadRulesAsync(ExchangeAdapter adapter, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(s_fetchTimeout);
        try
        {
            var rules = await engine.FetchPairRulesAsync(adapter, timeout.Token);
            pairRules.Save(rules);
            logger.LogInformation("{Exchange}: pair rules {Rules}", adapter.Name, rules);
            return rules;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested
            && e is HttpRequestException or OperationCanceledException or FormatException or JsonException
                or KeyNotFoundException or InvalidOperationException)
        {
            var cached = pairRules.Get(adapter.Id, adapter.Pair);
            if (cached is null)
            {
                logger.LogWarning("{Exchange}: pair rules could not be fetched and none are cached, no book ({Error})", adapter.Name, e.Message);
            }
            else
            {
                logger.LogWarning("{Exchange}: pair rules could not be fetched, using the cached {Rules} ({Error})", adapter.Name, cached, e.Message);
            }
            return cached;
        }
    }

    private async Task StartEngineAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rules = await Task.WhenAll(engine.Adapters
                .Where(adapter => adapter.PairRulesUri is not null)
                .Select(adapter => LoadRulesAsync(adapter, cancellationToken)));
            if (!cancellationToken.IsCancellationRequested)
            {
                engine.Start([.. rules.OfType<PairRules>()]);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
