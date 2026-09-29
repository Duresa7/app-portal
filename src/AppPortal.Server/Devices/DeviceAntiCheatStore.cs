using AppPortal.Server.Data;
using AppPortal.Shared;

namespace AppPortal.Server.Devices;

/// <summary>
/// The kernel anti-cheat each device's agent last reported. Read by the device page, which lists it for
/// the administrator; nothing decides anything from it.
/// </summary>
public sealed class DeviceAntiCheatStore(Database database)
{
    /// <summary>The longest value kept in any column. Every value the agent sends is a short name.</summary>
    public const int MaxLength = 64;

    private static readonly string[] Types = ["service", "driver"];

    public IReadOnlyList<DeviceAntiCheat> ForDevice(string deviceId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT product, service, type, state, start_type FROM device_anticheat
            WHERE device_id = @device ORDER BY product, service;
            """;
        command.Parameters.AddWithValue("@device", deviceId);
        using var reader = command.ExecuteReader();
        var found = new List<DeviceAntiCheat>();
        while (reader.Read())
        {
            found.Add(new DeviceAntiCheat(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4)));
        }

        return found;
    }

    /// <summary>
    /// Replaces everything this device reports, an empty list included, so a product somebody removed
    /// leaves the record. Only services this server knows as anti-cheat are kept, under the product
    /// name this server gives them: the agent's list is a report, and the names shown are the server's.
    /// </summary>
    public void Replace(string deviceId, IReadOnlyList<DeviceAntiCheat> found)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "DELETE FROM device_anticheat WHERE device_id = @device;";
            clear.Parameters.AddWithValue("@device", deviceId);
            clear.ExecuteNonQuery();
        }

        var seenAt = DateTimeOffset.UtcNow.ToString("O");
        var known = found
            .Select(row => (Row: row, Product: AntiCheats.Find(row.Service?.Trim())))
            .Where(item => item.Product is not null)
            .DistinctBy(item => item.Row.Service.Trim().ToUpperInvariant());
        foreach (var (row, product) in known)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO device_anticheat (device_id, service, product, type, state, start_type, seen_at)
                VALUES (@device, @service, @product, @type, @state, @start, @seen);
                """;
            insert.Parameters.AddWithValue("@device", deviceId);
            insert.Parameters.AddWithValue("@service", row.Service.Trim());
            insert.Parameters.AddWithValue("@product", product!.Name);
            insert.Parameters.AddWithValue("@type", Types.Contains(row.Type) ? row.Type : "service");
            insert.Parameters.AddWithValue("@state", Clip(row.State));
            insert.Parameters.AddWithValue("@start", Clip(row.StartType));
            insert.Parameters.AddWithValue("@seen", seenAt);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static string Clip(string? value)
    {
        var text = (value ?? "").Trim().ToLowerInvariant();
        return text.Length <= MaxLength ? text : text[..MaxLength];
    }
}
