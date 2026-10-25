namespace Marketgrade.Engine.Books;

/// <summary>Fetches pair rules from each exchange's public REST API.</summary>
public static class PairRulesClient
{
    /// <summary>Throws <see cref="HttpRequestException"/>, <see cref="FormatException"/> and the like when the exchange cannot be reached or answers unexpectedly.</summary>
    public static async Task<PairRules> FetchAsync(HttpClient http, ExchangeAdapter adapter, CancellationToken cancellationToken)
    {
        var uri = adapter.PairRulesUri ?? throw new NotSupportedException($"{adapter.Name} has no pair rules");
        using var response = await http.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var rules = adapter.ParsePairRules(body);
        if (rules.PriceDecimals is < 0 or > ScaledDecimal.MaxDecimals || rules.AmountDecimals is < 0 or > ScaledDecimal.MaxDecimals)
        {
            throw new FormatException($"{adapter.Name} pair rules out of range: {rules}");
        }
        return rules;
    }
}
