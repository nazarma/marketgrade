namespace Marketgrade.Engine.Books;

/// <summary>How many decimals prices and amounts of one pair have; books store both as longs scaled by them.</summary>
public sealed record PairRules(int ExchangeId, string Symbol, int PriceDecimals, int AmountDecimals)
{
    /// <summary>The decimals of a tick or step size: <c>"0.01000000"</c> has 2, <c>"1.0"</c> and <c>"10"</c> have 0.</summary>
    public static int DecimalsFrom(string tick)
    {
        var text = tick.AsSpan().Trim();
        if (text.IsEmpty || text.IndexOfAnyExcept("0123456789.") >= 0 || text.Count('.') > 1 || text[0] == '.' || text[^1] == '.')
        {
            throw new FormatException($"Not a tick size: {tick}");
        }
        var point = text.IndexOf('.');
        var decimals = point < 0 ? 0 : text[(point + 1)..].TrimEnd('0').Length;
        if (decimals > ScaledDecimal.MaxDecimals)
        {
            throw new FormatException($"Tick size {tick} has more than {ScaledDecimal.MaxDecimals} decimals");
        }
        return decimals;
    }
}
