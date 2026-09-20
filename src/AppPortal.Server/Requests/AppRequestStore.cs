using AppPortal.Server.Data;
using AppPortal.Shared;

using Microsoft.Data.Sqlite;

namespace AppPortal.Server.Requests;

/// <summary>One request for software that is not in the catalog, and where it stands.</summary>
public sealed class AppRequestRecord
{
    public string Id { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string? RequestedBy { get; set; }
    public string Text { get; set; } = "";
    public AppRequestStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public AppRequest ToPublic()
        => new(Id, Text, DeviceName, RequestedBy, Status, Reason, CreatedAt, DecidedAt);
}

/// <summary>Raised when a request cannot be stored as asked.</summary>
public enum AppRequestRejection
{
    Empty,
    TooLong,
    TooManyPending,
}

public sealed class AppRequestRejectedException(AppRequestRejection reason, string message) : Exception(message)
{
    public AppRequestRejection Reason { get; } = reason;
}

/// <summary>Free-text requests, one row each, in the database under the data directory.</summary>
public sealed class AppRequestStore(Database database)
{
    public AppRequestRecord Create(string deviceName, string? requestedBy, string text)
        => CreateCore(deviceName, byId: false, requestedBy, text);

    public AppRequestRecord CreateForDeviceId(string deviceId, string? requestedBy, string text)
        => CreateCore(deviceId, byId: true, requestedBy, text);

    private AppRequestRecord CreateCore(string identity, bool byId, string? requestedBy, string text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            throw new AppRequestRejectedException(AppRequestRejection.Empty, "Say what you would like installed.");
        }

        if (trimmed.Length > AppRequestLimits.MaxTextLength)
        {
            throw new AppRequestRejectedException(
                AppRequestRejection.TooLong,
                $"A request may be at most {AppRequestLimits.MaxTextLength} characters.");
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var (deviceId, deviceName) = Device(connection, transaction, identity, byId);

        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM app_requests WHERE device_id = @device AND status = 'pending';";
            count.Parameters.AddWithValue("@device", deviceId);
            if (Convert.ToInt32(count.ExecuteScalar()) >= AppRequestLimits.MaxPendingPerDevice)
            {
                throw new AppRequestRejectedException(
                    AppRequestRejection.TooManyPending,
                    "This device has too many requests waiting for an answer. Wait for one to be decided.");
            }
        }

        var record = new AppRequestRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            DeviceName = deviceName,
            RequestedBy = requestedBy,
            Text = trimmed,
            Status = AppRequestStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        using (var write = connection.CreateCommand())
        {
            write.Transaction = transaction;
            write.CommandText = """
                INSERT INTO app_requests (id, device_id, device_name, requested_by, text, status, reason, decided_by, decided_at, created_at)
                VALUES (@id, @device, @deviceName, @by, @text, 'pending', NULL, NULL, NULL, @created);
                """;
            write.Parameters.AddWithValue("@id", record.Id);
            write.Parameters.AddWithValue("@device", deviceId);
            write.Parameters.AddWithValue("@deviceName", deviceName);
            write.Parameters.AddWithValue("@by", (object?)record.RequestedBy ?? DBNull.Value);
            write.Parameters.AddWithValue("@text", record.Text);
            write.Parameters.AddWithValue("@created", SqlTime.From(record.CreatedAt));
            write.ExecuteNonQuery();
        }

        transaction.Commit();
        return record;
    }

    public IReadOnlyList<AppRequestRecord> ListForDevice(string deviceName)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE d.name = @name COLLATE NOCASE ORDER BY r.created_at DESC;";
        command.Parameters.AddWithValue("@name", deviceName);
        return Read(command);
    }

    public IReadOnlyList<AppRequestRecord> ListForDeviceId(string deviceId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE r.device_id = @device ORDER BY r.created_at DESC;";
        command.Parameters.AddWithValue("@device", deviceId);
        return Read(command);
    }

    /// <summary>Used by the admin pages in M1-05. A null status means every request.</summary>
    public IReadOnlyList<AppRequestRecord> ListByStatus(AppRequestStatus? status, int limit, int offset)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select
                              + (status is null ? "" : " WHERE r.status = @status")
                              + " ORDER BY r.created_at DESC LIMIT @limit OFFSET @offset;";
        if (status is not null)
        {
            command.Parameters.AddWithValue("@status", Name(status.Value));
        }

        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@offset", offset);
        return Read(command);
    }

    public AppRequestRecord? Find(string id)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE r.id = @id;";
        command.Parameters.AddWithValue("@id", id);
        return Read(command).FirstOrDefault();
    }

    /// <summary>Records an administrator's answer. Used by M1-05; false when the request is already decided.</summary>
    public bool Decide(string id, AppRequestStatus status, string? reason, string decidedBy)
    {
        if (status == AppRequestStatus.Pending)
        {
            throw new ArgumentException("A decision is approved or denied, not pending.", nameof(status));
        }

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE app_requests
            SET status = @status, reason = @reason, decided_by = @by, decided_at = @at
            WHERE id = @id AND status = 'pending';
            """;
        command.Parameters.AddWithValue("@status", Name(status));
        command.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
        command.Parameters.AddWithValue("@by", decidedBy);
        command.Parameters.AddWithValue("@at", SqlTime.Now());
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQuery() == 1;
    }

    public int PendingCount()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM app_requests WHERE status = 'pending';";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string Name(AppRequestStatus status) => status.ToString().ToLowerInvariant();

    private static (string Id, string Name) Device(SqliteConnection connection, SqliteTransaction transaction, string identity, bool byId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = byId
            ? "SELECT id, name FROM devices WHERE id = @identity;"
            : "SELECT id, name FROM devices WHERE name = @identity COLLATE NOCASE;";
        command.Parameters.AddWithValue("@identity", identity);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetString(0), reader.GetString(1))
            : throw new InvalidOperationException($"No device '{identity}' is registered, so its request cannot be recorded.");
    }

    private const string Select = """
        SELECT r.id, COALESCE(d.name, r.device_name), r.requested_by, r.text, r.status, r.reason, r.decided_by, r.decided_at, r.created_at
        FROM app_requests r
        LEFT JOIN devices d ON d.id = r.device_id
        """;

    private static List<AppRequestRecord> Read(SqliteCommand command)
    {
        var records = new List<AppRequestRecord>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new AppRequestRecord
            {
                Id = reader.GetString(0),
                DeviceName = reader.GetString(1),
                RequestedBy = reader.IsDBNull(2) ? null : reader.GetString(2),
                Text = reader.GetString(3),
                Status = ParseStatus(reader.GetString(4)),
                Reason = reader.IsDBNull(5) ? null : reader.GetString(5),
                DecidedBy = reader.IsDBNull(6) ? null : reader.GetString(6),
                DecidedAt = SqlTime.ParseOptional(reader.IsDBNull(7) ? null : reader.GetString(7)),
                CreatedAt = SqlTime.Parse(reader.GetString(8)),
            });
        }

        return records;
    }

    private static AppRequestStatus ParseStatus(string text)
        => Enum.TryParse<AppRequestStatus>(text, ignoreCase: true, out var status) ? status : AppRequestStatus.Pending;
}
