using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Server.Options;

using Microsoft.Extensions.Options;

namespace AppPortal.Server.Data;

/// <summary>
/// Moves a 0.2.x deployment's three JSON files into the database, once. Each table is imported only
/// while it is empty, so a later edit to a file never overwrites what the server has since been told.
/// </summary>
public sealed class LegacyImport(
    Database database,
    CatalogStore catalog,
    IOptions<PortalOptions> options,
    IHostEnvironment env,
    ILogger<LegacyImport> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public void Run()
    {
        ImportCatalog();
        ImportDevices();
        ImportInstalls();
    }

    private void ImportCatalog()
    {
        if (catalog.Count() > 0 || !File.Exists(catalog.SeedPath))
        {
            return;
        }

        try
        {
            var entries = CatalogStore.Parse(File.ReadAllText(catalog.SeedPath));
            catalog.Import(entries);
            logger.LogInformation("Imported {Count} catalog apps from {Path}", entries.Count, catalog.SeedPath);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or PrerequisiteException)
        {
            // An unreadable seed file leaves an empty catalog rather than stopping the server: the
            // operator can fix the file and run `catalog import`, and devices meanwhile see no apps.
            logger.LogError(ex, "Catalog file {Path} is invalid and was not imported", catalog.SeedPath);
        }
    }

    private void ImportDevices()
    {
        var path = Resolve(options.Value.DevicesPath);
        if (!File.Exists(path) || Count("devices") > 0)
        {
            return;
        }

        DevicesFile? file;
        try
        {
            file = JsonSerializer.Deserialize<DevicesFile>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogError(ex, "Device file {Path} could not be read and was not imported", path);
            return;
        }

        if (file is null || file.Devices.Count == 0)
        {
            return;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var device in file.Devices)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO devices (id, name, token_hash, enabled, action1_endpoint_id, has_agent, created_at)
                VALUES (@id, @name, @hash, @enabled, @endpoint, 0, @created);
                """;
            command.Parameters.AddWithValue("@id", DeviceStore.NewId());
            command.Parameters.AddWithValue("@name", device.Name);
            command.Parameters.AddWithValue("@hash", device.TokenSha256);
            command.Parameters.AddWithValue("@enabled", device.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("@endpoint", string.IsNullOrEmpty(device.EndpointId) ? DBNull.Value : device.EndpointId);
            command.Parameters.AddWithValue("@created", SqlTime.From(device.CreatedAt == default ? DateTimeOffset.UtcNow : device.CreatedAt));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        logger.LogInformation("Imported {Count} devices from {Path}", file.Devices.Count, path);
    }

    private void ImportInstalls()
    {
        var path = Path.Combine(Resolve(options.Value.DataDirectory), "installs.json");
        if (!File.Exists(path) || Count("installs") > 0)
        {
            return;
        }

        List<InstallRecord>? records;
        try
        {
            records = JsonSerializer.Deserialize<List<InstallRecord>>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogError(ex, "Install history {Path} could not be read and was not imported", path);
            return;
        }

        if (records is null || records.Count == 0)
        {
            return;
        }

        var devices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var connection = database.Open();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT name, id FROM devices;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                devices[reader.GetString(0)] = reader.GetString(1);
            }
        }

        using var transaction = connection.BeginTransaction();
        var imported = 0;
        var skipped = 0;
        foreach (var record in records)
        {
            if (!devices.TryGetValue(record.DeviceName, out var deviceId))
            {
                skipped++;
                continue;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO installs (id, device_id, app_id, app_name, requested_by, engine, external_ref,
                                      state, percent, detail, requested_at, completed_at, last_checked_at)
                VALUES (@id, @device, @appId, @appName, NULL, 'action1', @external,
                        @state, @percent, @detail, @requested, @completed, @checked);
                """;
            command.Parameters.AddWithValue("@id", record.Id);
            command.Parameters.AddWithValue("@device", deviceId);
            command.Parameters.AddWithValue("@appId", record.AppId);
            command.Parameters.AddWithValue("@appName", record.AppName);
            command.Parameters.AddWithValue("@external", (object?)record.AutomationId ?? DBNull.Value);
            command.Parameters.AddWithValue("@state", record.State.ToString());
            command.Parameters.AddWithValue("@percent", record.PercentComplete);
            command.Parameters.AddWithValue("@detail", (object?)record.Detail ?? DBNull.Value);
            command.Parameters.AddWithValue("@requested", SqlTime.From(record.RequestedAt));
            command.Parameters.AddWithValue("@completed", (object?)SqlTime.FromOptional(record.CompletedAt) ?? DBNull.Value);
            command.Parameters.AddWithValue("@checked", SqlTime.From(record.LastCheckedAt ?? record.RequestedAt));
            command.ExecuteNonQuery();
            imported++;
        }

        transaction.Commit();
        logger.LogInformation("Imported {Count} install records from {Path}", imported, path);
        if (skipped > 0)
        {
            logger.LogWarning("{Count} install records named a device that is no longer registered and were left behind", skipped);
        }
    }

    private int Count(string table)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private string Resolve(string path)
        => Path.IsPathRooted(path) ? path : Path.Combine(env.ContentRootPath, path);
}
