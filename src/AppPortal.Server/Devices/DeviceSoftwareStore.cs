using AppPortal.Server.Data;
using AppPortal.Shared;

namespace AppPortal.Server.Devices;

/// <summary>
/// What each device's agent last said was installed on it. Replaced whole on every report rather than
/// merged, because the agent sends the complete list and software that has gone must go from here too.
/// </summary>
public sealed class DeviceSoftwareStore(Database database)
{
    /// <summary>
    /// What this device carries for everyone, plus what it carries for one account. Another person's
    /// per-user software is never returned: it is theirs, and the person asking did not install it.
    /// </summary>
    public IReadOnlyList<InstalledSoftware> ForDevice(string deviceId, string? account = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, version FROM device_software
            WHERE device_id = @device AND (account = '' OR account = @account COLLATE NOCASE)
            ORDER BY name;
            """;
        command.Parameters.AddWithValue("@device", deviceId);
        command.Parameters.AddWithValue("@account", (object?)account ?? "");
        using var reader = command.ExecuteReader();
        var found = new List<InstalledSoftware>();
        while (reader.Read())
        {
            found.Add(new InstalledSoftware(reader.GetString(0), reader.GetString(1)));
        }

        return found;
    }

    /// <summary>
    /// Replaces what this device reports for one scope. Machine-wide and each account are separate
    /// lists: a sweep of one must not erase the others, which were gathered by a different run.
    /// </summary>
    public void Replace(string deviceId, IReadOnlyList<InstalledSoftware> software, string? account = null)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM device_software WHERE device_id = @device AND account = @account;";
            clear.Parameters.AddWithValue("@device", deviceId);
            clear.Parameters.AddWithValue("@account", (object?)account ?? "");
            clear.ExecuteNonQuery();
        }

        var seenAt = DateTimeOffset.UtcNow.ToString("O");

        // Trim before comparing, or "Steam" and " Steam " are two pieces of software. The primary key
        // on this table is case-sensitive as SQLite compares text, so the duplicate has to be settled
        // here rather than left to the insert.
        var cleaned = software
            .Select(item => new InstalledSoftware(item.Name?.Trim() ?? "", item.Version?.Trim() ?? ""))
            .Where(item => item.Name.Length > 0)
            .DistinctBy(item => item.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var item in cleaned)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO device_software (device_id, account, name, version, seen_at)
                VALUES (@device, @account, @name, @version, @seen)
                ON CONFLICT (device_id, account, name)
                DO UPDATE SET version = excluded.version, seen_at = excluded.seen_at;
                """;
            insert.Parameters.AddWithValue("@device", deviceId);
            insert.Parameters.AddWithValue("@account", (object?)account ?? "");
            insert.Parameters.AddWithValue("@name", item.Name);
            insert.Parameters.AddWithValue("@version", item.Version);
            insert.Parameters.AddWithValue("@seen", seenAt);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
