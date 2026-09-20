using AppPortal.Server.Data;
using AppPortal.Shared;

using Microsoft.Data.Sqlite;

namespace AppPortal.Server.Installs;

public sealed class InstallRecord
{
    public string Id { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string EndpointId { get; set; } = "";
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";

    /// <summary>
    /// The package this install was started from. Not persisted: nothing reads it after the deployment
    /// has been started, and the app's current package is always in the catalog under <see cref="AppId"/>.
    /// </summary>
    public string PackageId { get; set; } = "";
    public string Version { get; set; } = "";

    public string? AutomationId { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public InstallState State { get; set; }
    public int PercentComplete { get; set; }
    public string? Detail { get; set; }

    /// <summary>
    /// The signed-in Windows account that asked for this install, as <c>DOMAIN\user</c>, or null when the
    /// client did not say. Informational: the device token is what proves the caller.
    /// </summary>
    public string? RequestedBy { get; set; }

    public bool IsActive => State is InstallState.Queued or InstallState.Running;

    public InstallRequest ToPublic()
        => new(Id, AppId, AppName, DeviceName, RequestedAt, CompletedAt, State, PercentComplete, Detail, RequestedBy);
}

/// <summary>Install history, one row per request, in the database under the data directory.</summary>
public sealed class InstallStore(Database database)
{
    public IReadOnlyList<InstallRecord> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " ORDER BY i.requested_at DESC;";
        return Read(command);
    }

    public IReadOnlyList<InstallRecord> ForDevice(string deviceName)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE COALESCE(NULLIF(i.device_name, ''), d.name) = @name COLLATE NOCASE ORDER BY i.requested_at DESC;";
        command.Parameters.AddWithValue("@name", deviceName);
        return Read(command);
    }

    public InstallRecord? Find(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE i.id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return Read(command).FirstOrDefault();
    }

    /// <summary>
    /// Writes a record, unless the caller is working from an older snapshot than what is stored.
    /// The background poller and any number of HTTP requests refresh the same install concurrently,
    /// each from its own snapshot and its own round trip to Action1. Without this guard a slow
    /// earlier call can land after a fast later one and push a finished install back to Running.
    /// Returns false when the write was dropped as stale.
    /// </summary>
    public bool Upsert(InstallRecord record)
    {
        using var connection = database.Open();
        // Immediate takes the write lock now rather than at the first write, so two callers reading the
        // stored row and deciding whether to overwrite it cannot interleave.
        using var transaction = connection.BeginTransaction(deferred: false);

        string? storedState = null;
        string? storedChecked = null;
        using (var stored = connection.CreateCommand())
        {
            stored.Transaction = transaction;
            stored.CommandText = "SELECT state, last_checked_at FROM installs WHERE id = @id;";
            stored.Parameters.AddWithValue("@id", record.Id);
            using var reader = stored.ExecuteReader();
            if (reader.Read())
            {
                storedState = reader.GetString(0);
                storedChecked = reader.GetString(1);
            }
        }

        if (storedState is not null)
        {
            var storedIsActive = ParseState(storedState) is InstallState.Queued or InstallState.Running;
            if (!storedIsActive && record.IsActive)
            {
                return false;
            }

            if (record.LastCheckedAt is { } incoming && incoming < SqlTime.Parse(storedChecked!))
            {
                return false;
            }
        }

        using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = """
                INSERT INTO installs (id, device_id, device_name, app_id, app_name, requested_by, engine, external_ref,
                                      state, percent, detail, requested_at, completed_at, last_checked_at)
                VALUES (@id, @device, @deviceName, @appId, @appName, @requestedBy, 'action1', @external,
                        @state, @percent, @detail, @requested, @completed, @checked)
                ON CONFLICT(id) DO UPDATE SET
                    app_name = excluded.app_name, external_ref = excluded.external_ref, state = excluded.state,
                    percent = excluded.percent, detail = excluded.detail, completed_at = excluded.completed_at,
                    last_checked_at = excluded.last_checked_at;
                """;
            write.Parameters.AddWithValue("@id", record.Id);
            // Always supplied: the column is not null, and SQLite checks that before it decides the row conflicts.
            write.Parameters.AddWithValue("@device", DeviceId(connection, transaction, record.DeviceName));
            // Denormalised on purpose: this is what the history shows once the device itself is gone.
            write.Parameters.AddWithValue("@deviceName", record.DeviceName);
            write.Parameters.AddWithValue("@appId", record.AppId);
            write.Parameters.AddWithValue("@appName", record.AppName);
            // Left out of the ON CONFLICT update on purpose: who asked is settled when the install is made,
            // and the poller refreshes from records that never carried it.
            write.Parameters.AddWithValue("@requestedBy", (object?)record.RequestedBy ?? DBNull.Value);
            write.Parameters.AddWithValue("@external", (object?)record.AutomationId ?? DBNull.Value);
            write.Parameters.AddWithValue("@state", record.State.ToString());
            write.Parameters.AddWithValue("@percent", record.PercentComplete);
            write.Parameters.AddWithValue("@detail", (object?)record.Detail ?? DBNull.Value);
            write.Parameters.AddWithValue("@requested", SqlTime.From(record.RequestedAt));
            write.Parameters.AddWithValue("@completed", (object?)SqlTime.FromOptional(record.CompletedAt) ?? DBNull.Value);
            // The column is not null: an install that has never been refreshed was last seen when it was made.
            write.Parameters.AddWithValue("@checked", SqlTime.From(record.LastCheckedAt ?? record.RequestedAt));
            write.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    // LEFT JOIN, and the name off the install rather than the device: a removed device leaves its
    // history behind, and an inner join would have quietly deleted that history from every page.
    private const string Select = """
        SELECT i.id, COALESCE(NULLIF(i.device_name, ''), d.name, '') AS device_name, d.action1_endpoint_id,
               i.app_id, i.app_name, i.external_ref,
               i.state, i.percent, i.detail, i.requested_at, i.completed_at, i.last_checked_at,
               i.requested_by
        FROM installs i
        LEFT JOIN devices d ON d.id = i.device_id
        """;

    private static List<InstallRecord> Read(SqliteCommand command)
    {
        var records = new List<InstallRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new InstallRecord
            {
                Id = reader.GetString(0),
                DeviceName = reader.GetString(1),
                EndpointId = reader.IsDBNull(2) ? "" : reader.GetString(2),
                AppId = reader.GetString(3),
                AppName = reader.GetString(4),
                AutomationId = reader.IsDBNull(5) ? null : reader.GetString(5),
                State = ParseState(reader.GetString(6)),
                PercentComplete = (int)reader.GetInt64(7),
                Detail = reader.IsDBNull(8) ? null : reader.GetString(8),
                RequestedAt = SqlTime.Parse(reader.GetString(9)),
                CompletedAt = SqlTime.ParseOptional(reader.IsDBNull(10) ? null : reader.GetString(10)),
                LastCheckedAt = SqlTime.Parse(reader.GetString(11)),
                RequestedBy = reader.IsDBNull(12) ? null : reader.GetString(12),
            });
        }

        return records;
    }

    private static string DeviceId(SqliteConnection connection, SqliteTransaction transaction, string deviceName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM devices WHERE name = @name COLLATE NOCASE;";
        command.Parameters.AddWithValue("@name", deviceName);
        return command.ExecuteScalar() as string
               ?? throw new InvalidOperationException($"No device named '{deviceName}' is registered, so its install cannot be recorded.");
    }

    private static InstallState ParseState(string text)
        => Enum.TryParse<InstallState>(text, ignoreCase: true, out var state) ? state : InstallState.Failed;
}
