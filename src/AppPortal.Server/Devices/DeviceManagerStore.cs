using AppPortal.Server.Data;
using AppPortal.Shared;

namespace AppPortal.Server.Devices;

/// <summary>
/// The package managers each device's agent last reported. Read by the device page, which lists them,
/// and by the catalog page, which says how many devices could install through a given one.
/// </summary>
public sealed class DeviceManagerStore(Database database)
{
    /// <summary>Longest version string kept. The agent sends the first line a manager prints.</summary>
    public const int MaxVersionLength = 64;

    public IReadOnlyList<DeviceManager> ForDevice(string deviceId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT manager, version, account FROM device_managers
            WHERE device_id = @device ORDER BY manager, account;
            """;
        command.Parameters.AddWithValue("@device", deviceId);
        using var reader = command.ExecuteReader();
        var found = new List<DeviceManager>();
        while (reader.Read())
        {
            var account = reader.GetString(2);
            found.Add(new DeviceManager(reader.GetString(0), reader.GetString(1), account.Length == 0 ? null : account));
        }

        return found;
    }

    /// <summary>
    /// How many devices report each manager, for anyone. A device counts once however many of its
    /// profiles carry the manager, because the question on the catalog page is how many PCs could run
    /// the install, not how many people.
    /// </summary>
    public IReadOnlyDictionary<string, int> DevicesByManager()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT manager, COUNT(DISTINCT device_id) FROM device_managers GROUP BY manager;";
        using var reader = command.ExecuteReader();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read())
        {
            counts[reader.GetString(0)] = reader.GetInt32(1);
        }

        return counts;
    }

    /// <summary>
    /// Replaces everything this device reports. The whole list every time, because a manager somebody
    /// uninstalled has to be able to leave the record, which a merge could never express.
    /// </summary>
    public void Replace(string deviceId, IReadOnlyList<DeviceManager> managers)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM device_managers WHERE device_id = @device;";
            clear.Parameters.AddWithValue("@device", deviceId);
            clear.ExecuteNonQuery();
        }

        var seenAt = DateTimeOffset.UtcNow.ToString("O");
        // Only managers this server knows, each once. A newer agent may report one this server has
        // never heard of, and a name nobody can choose on the catalog page is not worth keeping.
        var known = managers
            .Where(m => PackageManagers.Find(m.Name?.Trim()) is not null)
            .Select(m => (Name: m.Name.Trim(), Account: m.Account?.Trim() ?? "", Version: Clip(m.Version)))
            .DistinctBy(m => (m.Name, m.Account.ToUpperInvariant()));
        foreach (var (name, account, version) in known)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO device_managers (device_id, manager, account, version, seen_at)
                VALUES (@device, @manager, @account, @version, @seen);
                """;
            insert.Parameters.AddWithValue("@device", deviceId);
            insert.Parameters.AddWithValue("@manager", name);
            insert.Parameters.AddWithValue("@account", account);
            insert.Parameters.AddWithValue("@version", version);
            insert.Parameters.AddWithValue("@seen", seenAt);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string Clip(string? version)
    {
        var text = version?.Trim() ?? "";
        return text.Length <= MaxVersionLength ? text : text[..MaxVersionLength];
    }
}
