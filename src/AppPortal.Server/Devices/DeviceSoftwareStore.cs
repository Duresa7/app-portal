using AppPortal.Server.Data;
using AppPortal.Shared;

namespace AppPortal.Server.Devices;

/// <summary>One row of a device's inventory, and which tool found it: winget or a package manager.</summary>
public sealed record ReportedSoftware(string Name, string Version, string Source);

/// <summary>
/// What each device's agent last said was installed on it. Replaced whole on every report rather than
/// merged, because the agent sends the complete list and software that has gone must go from here too.
/// Each source is its own list: winget's sweep and each package manager's arrive separately.
/// </summary>
public sealed class DeviceSoftwareStore(Database database)
{
    /// <summary>The source of the sweep every agent has always sent, and of every row from before sources.</summary>
    public const string WingetSource = "winget";

    /// <summary>
    /// What this device carries for everyone, plus what it carries for one account. Another person's
    /// per-user software is never returned: it is theirs, and the person asking did not install it.
    /// </summary>
    public IReadOnlyList<ReportedSoftware> ForDevice(string deviceId, string? account = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, version, source FROM device_software
            WHERE device_id = @device AND (account = '' OR account = @account COLLATE NOCASE)
            ORDER BY name;
            """;
        command.Parameters.AddWithValue("@device", deviceId);
        command.Parameters.AddWithValue("@account", (object?)account ?? "");
        using var reader = command.ExecuteReader();
        var found = new List<ReportedSoftware>();
        while (reader.Read())
        {
            found.Add(new ReportedSoftware(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return found;
    }

    /// <summary>
    /// Replaces what this device reports for one scope and one source. Machine-wide and each account
    /// are separate lists, and so are winget and each package manager: a sweep of one must not erase
    /// the others, which were gathered by a different run.
    /// </summary>
    public void Replace(string deviceId, IReadOnlyList<InstalledSoftware> software, string? account = null,
        string source = WingetSource)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM device_software WHERE device_id = @device AND account = @account AND source = @source;";
            clear.Parameters.AddWithValue("@device", deviceId);
            clear.Parameters.AddWithValue("@source", source);
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
                INSERT INTO device_software (device_id, account, source, name, version, seen_at)
                VALUES (@device, @account, @source, @name, @version, @seen)
                ON CONFLICT (device_id, account, source, name)
                DO UPDATE SET version = excluded.version, seen_at = excluded.seen_at;
                """;
            insert.Parameters.AddWithValue("@device", deviceId);
            insert.Parameters.AddWithValue("@account", (object?)account ?? "");
            insert.Parameters.AddWithValue("@source", source);
            insert.Parameters.AddWithValue("@name", item.Name);
            insert.Parameters.AddWithValue("@version", item.Version);
            insert.Parameters.AddWithValue("@seen", seenAt);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
