using System.Globalization;
using Marketgrade.Engine.Books;

namespace Marketgrade.Backend;

/// <summary>The last pair rules fetched from each exchange, so the app also starts its books offline.</summary>
public sealed class PairRulesStore(SettingsStore store)
{
    public PairRules? Get(int exchangeId, string symbol)
    {
        using var connection = store.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT price_decimals, amount_decimals FROM pair_rules WHERE exchange_id = $exchangeId AND symbol = $symbol
            """;
        command.Parameters.AddWithValue("$exchangeId", exchangeId);
        command.Parameters.AddWithValue("$symbol", symbol);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new PairRules(exchangeId, symbol, reader.GetInt32(0), reader.GetInt32(1)) : null;
    }

    public void Save(PairRules rules)
    {
        using var connection = store.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pair_rules (exchange_id, symbol, price_decimals, amount_decimals, updated_at)
            VALUES ($exchangeId, $symbol, $priceDecimals, $amountDecimals, $updatedAt)
            ON CONFLICT (exchange_id, symbol) DO UPDATE SET
                price_decimals = excluded.price_decimals,
                amount_decimals = excluded.amount_decimals,
                updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$exchangeId", rules.ExchangeId);
        command.Parameters.AddWithValue("$symbol", rules.Symbol);
        command.Parameters.AddWithValue("$priceDecimals", rules.PriceDecimals);
        command.Parameters.AddWithValue("$amountDecimals", rules.AmountDecimals);
        command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}
