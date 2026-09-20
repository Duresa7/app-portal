using System.Text.Json;

using AppPortal.Server.Data;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.Data.Sqlite;

namespace AppPortal.Server.Agent;

public sealed class AgentJobStore(Database database, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Create(InstallRecord install, PackageDefinition definition)
    {
        var id = Guid.NewGuid().ToString("N");
        install.Engine = EngineLabel.Agent;
        install.AutomationId = id;
        install.Detail = "Waiting for the agent.";
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        // The job must never be visible without its install, even if the server stops between writes.
        InstallStore.Upsert(install, connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_jobs (id, install_id, device_id, definition_json, state, created_at, updated_at)
            VALUES (@id, @install, @device, @definition, 'queued', @now, @now);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@install", install.Id);
        command.Parameters.AddWithValue("@device", install.DeviceId!);
        command.Parameters.AddWithValue("@definition", JsonSerializer.Serialize(definition, Json));
        command.Parameters.AddWithValue("@now", SqlTime.From(_time.GetUtcNow()));
        command.ExecuteNonQuery();
        transaction.Commit();
        return id;
    }

    public bool HasQueued(string deviceId)
    {
        RequeueExpired();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_jobs WHERE device_id = @device AND state = 'queued');";
        command.Parameters.AddWithValue("@device", deviceId);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    public AgentJob? Lease(string deviceId)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _time.GetUtcNow();
        RequeueExpired(connection, transaction, now);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_jobs SET state = 'leased', attempt = attempt + 1, leased_until = @until, updated_at = @now
            WHERE id = (SELECT id FROM agent_jobs WHERE device_id = @device AND state = 'queued'
                        ORDER BY created_at, rowid LIMIT 1)
              AND NOT EXISTS (SELECT 1 FROM agent_jobs WHERE device_id = @device
                              AND state IN ('leased', 'downloading', 'installing'))
            RETURNING id, install_id, definition_json, attempt;
            """;
        command.Parameters.AddWithValue("@device", deviceId);
        command.Parameters.AddWithValue("@until", SqlTime.From(now.AddMinutes(5)));
        command.Parameters.AddWithValue("@now", SqlTime.From(now));
        AgentJob? job = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                job = new AgentJob(reader.GetString(0), reader.GetString(1),
                    JsonSerializer.Deserialize<PackageDefinition>(reader.GetString(2), Json)!, reader.GetInt32(3));
            }
        }

        transaction.Commit();
        return job;
    }

    public bool Progress(string deviceId, string id, AgentJobProgress progress, int? attempt = null)
        => Update(deviceId, id, progress.State, progress.Percent, progress.Detail, attempt);

    public bool Complete(string deviceId, string id, AgentJobCompletion completion, int? attempt = null)
    {
        var detail = completion.Detail ?? (completion.Ok ? "Installed." : "Installation failed.");
        if (!completion.Ok && completion.ExitCode is { } code)
        {
            detail += $" (exit code {code})";
        }

        return Update(deviceId, id, completion.Ok ? "succeeded" : "failed", null, detail, attempt);
    }

    private bool Update(string deviceId, string id, string state, int? percent, string? detail, int? attempt)
    {
        if (state is not ("queued" or "downloading" or "installing" or "succeeded" or "failed" or "cancelled")
            || percent is < 0 or > 100)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        RequeueExpired(connection, transaction, now);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_jobs SET state = @state, updated_at = @now, leased_until = @until
            WHERE id = @id AND device_id = @device AND state IN ('leased', 'downloading', 'installing')
              AND (@attempt IS NULL OR attempt = @attempt)
              AND NOT (state = 'installing' AND @state = 'downloading')
            RETURNING install_id;
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@device", deviceId);
        command.Parameters.AddWithValue("@state", state);
        command.Parameters.AddWithValue("@now", SqlTime.From(now));
        command.Parameters.AddWithValue("@until", state is "downloading" or "installing" ? SqlTime.From(now.AddMinutes(5)) : DBNull.Value);
        command.Parameters.AddWithValue("@attempt", (object?)attempt ?? DBNull.Value);
        string? installId;
        using (var reader = command.ExecuteReader())
        {
            installId = reader.Read() ? reader.GetString(0) : null;
        }

        if (installId is not null)
        {
            Mirror(connection, transaction, installId, state, percent, detail, now);
        }

        transaction.Commit();
        return installId is not null;
    }

    public void RequeueExpired()
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        RequeueExpired(connection, transaction, _time.GetUtcNow());
        transaction.Commit();
    }

    private static void RequeueExpired(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_jobs SET state = CASE WHEN attempt < 3 THEN 'queued' ELSE 'failed' END,
                                  leased_until = NULL, updated_at = @now
            WHERE state IN ('leased', 'downloading', 'installing') AND leased_until <= @now
            RETURNING install_id, state;
            """;
        command.Parameters.AddWithValue("@now", SqlTime.From(now));
        var expired = new List<(string installId, string state)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                expired.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        foreach (var (installId, state) in expired)
        {
            Mirror(connection, transaction, installId, state, 0,
                state == "failed" ? "Agent lease expired after three attempts." : "Waiting for the agent to retry.", now);
        }
    }

    private static void Mirror(SqliteConnection connection, SqliteTransaction transaction, string installId,
        string state, int? percent, string? detail, DateTimeOffset now)
    {
        var installState = state switch
        {
            "queued" => InstallState.Queued,
            "succeeded" => InstallState.Succeeded,
            "failed" => InstallState.Failed,
            "cancelled" => InstallState.Cancelled,
            _ => InstallState.Running,
        };
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE installs SET state = @state, percent = COALESCE(@percent, percent), detail = @detail,
                                completed_at = @completed, last_checked_at = @now
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", installId);
        command.Parameters.AddWithValue("@state", installState.ToString());
        command.Parameters.AddWithValue("@percent", state == "succeeded" ? 100 : (object?)percent ?? DBNull.Value);
        command.Parameters.AddWithValue("@detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("@completed", installState is InstallState.Queued or InstallState.Running ? DBNull.Value : SqlTime.From(now));
        command.Parameters.AddWithValue("@now", SqlTime.From(now));
        command.ExecuteNonQuery();
    }
}
