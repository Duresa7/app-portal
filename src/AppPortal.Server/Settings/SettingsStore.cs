using AppPortal.Server.Data;
using AppPortal.Shared;

namespace AppPortal.Server.Settings;

/// <summary>
/// Settings the whole server shares, one row per key. Read on demand rather than cached: they change
/// rarely and are read on paths that already touch the database, so a cache would only add a way for
/// two servers on one volume to disagree.
/// </summary>
public sealed class SettingsStore(Database database)
{
    public const string DefaultEngineKey = "default_engine";

    /// <summary>
    /// Which engine wins when a device and an app could both be served either way. Action1 unless
    /// somebody has said otherwise, because it is the engine that existed first and a fleet upgrading
    /// into the agent should keep behaving as it did until that is a decision rather than an accident.
    /// </summary>
    public string DefaultEngine
    {
        get
        {
            var value = Get(DefaultEngineKey)?.ToLowerInvariant();
            return value is EngineLabel.Action1 or EngineLabel.Agent ? value : EngineLabel.Action1;
        }
    }

    public string? Get(string key)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    public void Set(string key, string value)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO settings (key, value) VALUES (@key, @value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }
}
