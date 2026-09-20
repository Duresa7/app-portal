using System.Globalization;
using System.Text.Json;

using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Data;
using AppPortal.Shared;

using Microsoft.Data.Sqlite;

namespace AppPortal.Server.Installs;

public sealed class InstallRecord
{
    public string Id { get; set; } = "";
    public string? DeviceId { get; set; }
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

    /// <summary>Which install engine carried this out.</summary>
    public string Engine { get; set; } = EngineLabel.Action1;

    /// <summary>
    /// Null when no restart is involved, <c>pending</c> while one is owed, <c>confirmed</c> once the
    /// device has restarted and the software was still there.
    /// </summary>
    public string? RebootState { get; set; }

    /// <summary>
    /// Which step of the chain is running, for an install that needed other apps first. Not stored:
    /// filled from the steps when the record is read out, so there is one place the truth lives.
    /// </summary>
    public string? StepName { get; set; }

    public int StepNumber { get; set; }

    public int StepCount { get; set; }

    public bool IsWaitingForRestart => RebootState == AppPortal.Shared.RebootState.Pending;

    public string EngineText => EngineLabel.For(Engine);

    public bool IsActive => State is InstallState.Queued or InstallState.Running;

    public InstallRequest ToPublic()
        => new(Id, AppId, AppName, DeviceName, RequestedAt, CompletedAt, State, PercentComplete, Detail, RequestedBy, Engine, RebootState,
            StepName, StepNumber, StepCount);
}

/// <summary>Install history, one row per request, in the database under the data directory.</summary>
/// <summary>
/// What the install history is narrowed by. Every field is optional and they combine with AND, which
/// is how the filter row on the page reads: each control the administrator fills in narrows the result.
/// The dates are days as typed into the picker; the store turns them into a range, so the filter
/// round-trips through a URL exactly as it was written.
/// </summary>
public sealed record InstallFilter(
    string? Device = null,
    string? AppId = null,
    InstallState? State = null,
    string? Requester = null,
    DateOnly? From = null,
    DateOnly? To = null,
    bool AwaitingRestart = false) : IListFilter<InstallFilter>
{
    public static readonly InstallFilter None = new();

    public void Write(IDictionary<string, string?> query)
    {
        query.Put("Device", Device);
        query.Put("App", AppId);
        query.Put("State", State?.ToString());
        query.Put("Requester", Requester);
        query.Put("From", Day(From));
        query.Put("To", Day(To));
        query.Put("Restart", AwaitingRestart ? "1" : null);
    }

    public static InstallFilter Read(IReadOnlyDictionary<string, string?> query) => new(
        query.Get("Device"),
        query.Get("App"),
        Enum.TryParse<InstallState>(query.Get("State"), ignoreCase: true, out var state) ? state : null,
        query.Get("Requester"),
        ParseDay(query.Get("From")),
        ParseDay(query.Get("To")),
        query.Get("Restart") == "1");

    /// <summary>The form a date input speaks, and nothing else: a day is not a moment.</summary>
    public static string? Day(DateOnly? day) => day?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? ParseDay(string? text)
        => DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
}

public sealed class InstallStore(Database database)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The agent package for this app, whether or not it also has an Action1 one.</summary>
    public PackageDefinition? FindAgentPackage(string appId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT definition_json FROM catalog_packages WHERE app_id = @app AND engine = 'agent';";
        command.Parameters.AddWithValue("@app", appId);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<PackageDefinition>(json, Json) : null;
    }

    public PackageDefinition? FindAgentOnlyPackage(string appId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT definition_json FROM catalog_packages
            WHERE app_id = @app AND engine = 'agent'
              AND NOT EXISTS (SELECT 1 FROM catalog_packages WHERE app_id = @app AND engine = 'action1');
            """;
        command.Parameters.AddWithValue("@app", appId);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<PackageDefinition>(json, Json) : null;
    }

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

    /// <summary>
    /// The slice of the fleet-wide history a filter leaves, newest first unless the query sorts
    /// otherwise; the default ordering rides the installs_requested index. The total is counted only
    /// when asked for, because it is a second query; the installs page asks, since its pager shows it.
    /// </summary>
    public Slice<InstallRecord> List(InstallFilter filter, ListQuery query)
    {
        var orderBy = Sorts.OrderBy(query.Sort);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + Where(command, filter) + orderBy + " LIMIT @limit OFFSET @offset;";
        command.Parameters.AddWithValue("@limit", Slice.Lookahead(query));
        command.Parameters.AddWithValue("@offset", query.Offset);
        var window = Read(command);
        return Slice.FromLookahead(window, query, query.WantTotal ? Count(connection, filter) : null);
    }

    private static int Count(SqliteConnection connection, InstallFilter filter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM installs i LEFT JOIN devices d ON d.id = i.device_id" + Where(command, filter) + ";";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Installs in one state since a moment, for the dashboard tiles. A null state counts every one.</summary>
    public int CountBy(InstallState? state, DateTimeOffset? since)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        var clauses = new List<string>();
        if (state is not null)
        {
            clauses.Add("state = @state COLLATE NOCASE");
            command.Parameters.AddWithValue("@state", state.Value.ToString());
        }

        if (since is not null)
        {
            clauses.Add("requested_at >= @since");
            command.Parameters.AddWithValue("@since", SqlTime.From(since.Value));
        }

        command.CommandText = "SELECT COUNT(*) FROM installs"
                              + (clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses)) + ";";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>Installs still queued or running, which is what the tiles and the polling rows care about.</summary>
    public int CountActive()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM installs WHERE state IN ('Queued', 'Running') COLLATE NOCASE;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string Where(SqliteCommand command, InstallFilter filter)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(filter.Device))
        {
            clauses.Add("COALESCE(NULLIF(i.device_name, ''), d.name) = @device COLLATE NOCASE");
            command.Parameters.AddWithValue("@device", filter.Device.Trim());
        }

        if (!string.IsNullOrWhiteSpace(filter.AppId))
        {
            clauses.Add("i.app_id = @app COLLATE NOCASE");
            command.Parameters.AddWithValue("@app", filter.AppId.Trim());
        }

        if (filter.AwaitingRestart)
        {
            // Not a state of its own: these are running installs the device has to restart to finish,
            // and an administrator wants to find them without learning a new word for running.
            clauses.Add("i.reboot_state = @pending");
            command.Parameters.AddWithValue("@pending", RebootState.Pending);
        }

        if (filter.State is not null)
        {
            clauses.Add("i.state = @state COLLATE NOCASE");
            command.Parameters.AddWithValue("@state", filter.State.Value.ToString());
        }

        if (!string.IsNullOrWhiteSpace(filter.Requester))
        {
            // Substring, because an administrator looking for one person types a name, not DOMAIN\name.
            clauses.Add("i.requested_by LIKE @requester ESCAPE '\\'");
            command.Parameters.AddWithValue("@requester", "%" + Escape(filter.Requester.Trim()) + "%");
        }

        if (filter.From is { } from)
        {
            clauses.Add("i.requested_at >= @from");
            command.Parameters.AddWithValue("@from", SqlTime.From(StartOfDay(from)));
        }

        if (filter.To is { } to)
        {
            // A day in the To box means the whole of that day, so the range runs to the next midnight.
            clauses.Add("i.requested_at < @to");
            command.Parameters.AddWithValue("@to", SqlTime.From(StartOfDay(to.AddDays(1))));
        }

        return clauses.Count == 0 ? "" : " WHERE " + string.Join(" AND ", clauses);
    }

    /// <summary>
    /// A day out of the picker has no offset, and it means a day where the administrator is, which is
    /// the same clock the table's times are rendered on. Assuming UTC would slide the boundary by the
    /// server's offset and quietly drop or add a few hours' worth of rows at each end.
    /// </summary>
    private static DateTimeOffset StartOfDay(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local));

    /// <summary>Newest first is how the history reads; every other order is by request.</summary>
    private static readonly SortColumns Sorts = new(
        "i.requested_at DESC",
        ("requested", "i.requested_at"),
        ("device", "device_name"),
        ("requester", "i.requested_by"),
        ("app", "i.app_name"),
        ("state", "i.state"),
        ("completed", "i.completed_at"));

    /// <summary>A name with a wildcard in it should match that character, not every character.</summary>
    private static string Escape(string term)
        => term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public IReadOnlyList<InstallRecord> ForDeviceId(string deviceId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE i.device_id = @device ORDER BY i.requested_at DESC;";
        command.Parameters.AddWithValue("@device", deviceId);
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

        var written = Upsert(record, connection, transaction);
        transaction.Commit();
        return written;
    }

    internal static bool Upsert(InstallRecord record, SqliteConnection connection, SqliteTransaction transaction)
    {
        string? storedState = null;
        string? storedChecked = null;
        string? storedDeviceId = null;
        using (var stored = connection.CreateCommand())
        {
            stored.Transaction = transaction;
            stored.CommandText = "SELECT state, last_checked_at, device_id FROM installs WHERE id = @id;";
            stored.Parameters.AddWithValue("@id", record.Id);
            using var reader = stored.ExecuteReader();
            if (reader.Read())
            {
                storedState = reader.GetString(0);
                storedChecked = reader.GetString(1);
                storedDeviceId = reader.IsDBNull(2) ? null : reader.GetString(2);
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
                                      state, percent, detail, reboot_state, requested_at, completed_at, last_checked_at)
                VALUES (@id, @device, @deviceName, @appId, @appName, @requestedBy, @engine, @external,
                        @state, @percent, @detail, @reboot, @requested, @completed, @checked)
                ON CONFLICT(id) DO UPDATE SET
                    app_name = excluded.app_name, external_ref = excluded.external_ref, state = excluded.state,
                    percent = excluded.percent, detail = excluded.detail, reboot_state = excluded.reboot_state,
                    completed_at = excluded.completed_at, last_checked_at = excluded.last_checked_at;
                """;
            write.Parameters.AddWithValue("@id", record.Id);
            // Updates retain their original owner even if the device was renamed or removed.
            var deviceId = storedState is not null
                ? storedDeviceId
                : record.DeviceId ?? DeviceId(connection, transaction, record.DeviceName);
            write.Parameters.AddWithValue("@device", (object?)deviceId ?? DBNull.Value);
            // Denormalised on purpose: this is what the history shows once the device itself is gone.
            write.Parameters.AddWithValue("@deviceName", record.DeviceName);
            write.Parameters.AddWithValue("@appId", record.AppId);
            write.Parameters.AddWithValue("@appName", record.AppName);
            // Left out of the ON CONFLICT update on purpose: who asked is settled when the install is made,
            // and the poller refreshes from records that never carried it.
            write.Parameters.AddWithValue("@requestedBy", (object?)record.RequestedBy ?? DBNull.Value);
            write.Parameters.AddWithValue("@engine", record.Engine);
            write.Parameters.AddWithValue("@external", (object?)record.AutomationId ?? DBNull.Value);
            write.Parameters.AddWithValue("@state", record.State.ToString());
            write.Parameters.AddWithValue("@percent", record.PercentComplete);
            write.Parameters.AddWithValue("@detail", (object?)record.Detail ?? DBNull.Value);
            write.Parameters.AddWithValue("@reboot", (object?)record.RebootState ?? DBNull.Value);
            write.Parameters.AddWithValue("@requested", SqlTime.From(record.RequestedAt));
            write.Parameters.AddWithValue("@completed", (object?)SqlTime.FromOptional(record.CompletedAt) ?? DBNull.Value);
            // The column is not null: an install that has never been refreshed was last seen when it was made.
            write.Parameters.AddWithValue("@checked", SqlTime.From(record.LastCheckedAt ?? record.RequestedAt));
            write.ExecuteNonQuery();
        }

        return true;
    }

    // LEFT JOIN, and the name off the install rather than the device: a removed device leaves its
    // history behind, and an inner join would have quietly deleted that history from every page.
    private const string Select = """
        SELECT i.id, COALESCE(NULLIF(i.device_name, ''), d.name, '') AS device_name, d.action1_endpoint_id,
               i.app_id, i.app_name, i.external_ref,
               i.state, i.percent, i.detail, i.requested_at, i.completed_at, i.last_checked_at,
               i.requested_by, i.engine, i.device_id, i.reboot_state
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
                Engine = reader.IsDBNull(13) ? EngineLabel.Action1 : reader.GetString(13),
                DeviceId = reader.IsDBNull(14) ? null : reader.GetString(14),
                RebootState = reader.IsDBNull(15) ? null : reader.GetString(15),
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
