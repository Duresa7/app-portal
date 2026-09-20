using AppPortal.Server.Data;
using AppPortal.Shared;

namespace AppPortal.Server.Devices;

/// <summary>
/// What each device's agent last said was installed on it. Replaced whole on every report rather than
/// merged, because the agent sends the complete list and software that has gone must go from here too.
/// </summary>
public sealed class DeviceSoftwareStore(Database database)
{
    public IReadOnlyList<InstalledSoftware> ForDevice(string deviceId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, version FROM device_software WHERE device_id = @device ORDER BY name;";
        command.Parameters.AddWithValue("@device", deviceId);
        using var reader = command.ExecuteReader();
        var found = new List<InstalledSoftware>();
        while (reader.Read())
        {
            found.Add(new InstalledSoftware(reader.GetString(0), reader.GetString(1)));
        }

        return found;
    }

    public void Replace(string deviceId, IReadOnlyList<InstalledSoftware> software)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM device_software WHERE device_id = @device;";
            clear.Parameters.AddWithValue("@device", deviceId);
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
                INSERT INTO device_software (device_id, name, version, seen_at) VALUES (@device, @name, @version, @seen)
                ON CONFLICT (device_id, name) DO UPDATE SET version = excluded.version, seen_at = excluded.seen_at;
                """;
            insert.Parameters.AddWithValue("@device", deviceId);
            insert.Parameters.AddWithValue("@name", item.Name);
            insert.Parameters.AddWithValue("@version", item.Version);
            insert.Parameters.AddWithValue("@seen", seenAt);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
