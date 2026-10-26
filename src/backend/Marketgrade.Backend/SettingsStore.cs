using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Marketgrade.Backend;

/// <summary>User settings in SQLite: one row per section, its values as JSON. Other stores share its database and migrations.</summary>
public sealed class SettingsStore
{
    /// <summary>Applied in order; <c>PRAGMA user_version</c> is the number applied so far.</summary>
    private static readonly string[] s_migrations =
    [
        """
        CREATE TABLE settings (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        )
        """,
        """
        CREATE TABLE pair_rules (
            exchange_id INTEGER NOT NULL,
            symbol TEXT NOT NULL,
            price_decimals INTEGER NOT NULL,
            amount_decimals INTEGER NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (exchange_id, symbol)
        )
        """,
    ];

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString;

    public SettingsStore(string path)
    {
        Folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        Migrate();
    }

    /// <summary>The data folder the database is in.</summary>
    public string Folder { get; }

    public T? Get<T>(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, s_json) : default;
    }

    public void Set<T>(string key, T value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value, updated_at) VALUES ($key, $value, $updatedAt)
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value, s_json));
        command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Migrate()
    {
        using var connection = Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        for (var applied = (int)(long)version.ExecuteScalar()!; applied < s_migrations.Length; applied++)
        {
            using var transaction = connection.BeginTransaction();
            using var migration = connection.CreateCommand();
            migration.CommandText = $"{s_migrations[applied]};\nPRAGMA user_version = {applied + 1};";
            migration.ExecuteNonQuery();
            transaction.Commit();
        }
    }
}
