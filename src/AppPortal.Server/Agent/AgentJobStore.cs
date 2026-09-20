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
        install.Detail = install.IsUninstall ? "Waiting for the agent to remove it." : "Waiting for the agent.";
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        // The job must never be visible without its install, even if the server stops between writes.
        InstallStore.Upsert(install, connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_jobs (id, install_id, device_id, definition_json, state, requester, kind, created_at, updated_at)
            VALUES (@id, @install, @device, @definition, 'queued', @requester, @kind, @now, @now);
            """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@install", install.Id);
        command.Parameters.AddWithValue("@device", install.DeviceId!);
        command.Parameters.AddWithValue("@definition", JsonSerializer.Serialize(definition, Json));
        command.Parameters.AddWithValue("@requester", (object?)install.RequestedBy ?? DBNull.Value);
        command.Parameters.AddWithValue("@kind", install.Kind);
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
            RETURNING id, install_id, definition_json, attempt, requester, kind;
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
                    JsonSerializer.Deserialize<PackageDefinition>(reader.GetString(2), Json)!, reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? InstallKind.Install : reader.GetString(5));
            }
        }

        transaction.Commit();
        return job;
    }

    /// <summary>How many signed-in accounts one call may name, so a device cannot make work without end.</summary>
    private const int MaxAccounts = 32;

    /// <summary>
    /// Puts back into the queue any job parked for one of these accounts. A per-user install cannot run
    /// until the person who asked for it is signed in, and this is how the agent says they now are.
    /// A parked job holds no lease, so the installs behind it run past it rather than waiting.
    /// </summary>
    public int Resume(string deviceId, IReadOnlyList<string> accounts)
    {
        if (accounts.Count == 0)
        {
            return 0;
        }

        var now = _time.GetUtcNow();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var resumed = new List<string>();
        foreach (var account in accounts.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxAccounts))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Windows compares account names without regard to case, so this has to as well.
            command.CommandText = """
                UPDATE agent_jobs SET state = 'queued', updated_at = @now
                WHERE device_id = @device AND state = 'waiting_for_user'
                  AND requester IS NOT NULL AND requester = @account COLLATE NOCASE
                RETURNING install_id;
                """;
            command.Parameters.AddWithValue("@device", deviceId);
            command.Parameters.AddWithValue("@account", account);
            command.Parameters.AddWithValue("@now", SqlTime.From(now));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                resumed.Add(reader.GetString(0));
            }
        }

        foreach (var installId in resumed)
        {
            Mirror(connection, transaction, installId, "queued", 0, "Waiting for the agent.", now);
        }

        transaction.Commit();
        return resumed.Count;
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

        // Installed is not the same as finished. Software whose driver loads at boot is registered by
        // its installer and does nothing until the PC restarts, so the job is done and the install is
        // not: it stays running, saying what it is waiting for, until the restart confirms it.
        if (completion.Ok && completion.NeedsRestart)
        {
            return Update(deviceId, id, "succeeded", null, RebootState.WaitingDetail, attempt,
                reboot: RebootState.Pending, installState: InstallState.Running);
        }

        return Update(deviceId, id, completion.Ok ? "succeeded" : "failed", null, detail, attempt);
    }

    private bool Update(string deviceId, string id, string state, int? percent, string? detail, int? attempt,
        string? reboot = null, InstallState? installState = null)
    {
        if (state is not ("queued" or "downloading" or "installing" or "waiting_for_user"
                          or "succeeded" or "failed" or "cancelled")
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
            // A step that succeeded has not finished the install it belongs to. Deciding that here,
            // inside the same transaction, is what stops the card flashing "Installed" between one
            // step of a chain and the next.
            var forced = installState;
            var stepDetail = detail;
            if (state == "succeeded" && forced is null && HasStepAfter(connection, transaction, installId, id))
            {
                forced = InstallState.Running;
                stepDetail = "Installed. Moving on to the next step.";
            }
            else if (state is "failed" or "cancelled"
                     && StepLabel(connection, transaction, installId, id) is { } label)
            {
                // Which of the three, not just that one of them. A chain that says only "failed"
                // leaves an administrator to open every app in it to find out which.
                stepDetail = $"{label}: {detail}";
            }

            Mirror(connection, transaction, installId, state, percent, stepDetail, now, reboot, forced);
            MirrorStep(connection, transaction, installId, id, state, detail);
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

        FailAbandoned(connection, transaction, now);
    }

    /// <summary>
    /// Gives up on a job whose person never came back. A week is long enough for a holiday and short
    /// enough that the card does not sit there saying it is waiting for ever. It runs inside the sweep
    /// above rather than on its own, so it costs no extra connection and no extra transaction.
    /// </summary>
    private static void FailAbandoned(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_jobs SET state = 'failed', updated_at = @now
            WHERE state = 'waiting_for_user' AND updated_at <= @cutoff
            RETURNING install_id, requester;
            """;
        command.Parameters.AddWithValue("@now", SqlTime.From(now));
        command.Parameters.AddWithValue("@cutoff", SqlTime.From(now.AddDays(-7)));
        var abandoned = new List<(string InstallId, string? Requester)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                abandoned.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        foreach (var (installId, requester) in abandoned)
        {
            var who = string.IsNullOrWhiteSpace(requester) ? "the person who asked" : requester;
            Mirror(connection, transaction, installId, "failed", 0, $"Nobody signed in as {who} within 7 days.", now);
        }
    }

    /// <summary>
    /// How this job reads as a step, like "Step 2 of 3, A Launcher", or null when the install has
    /// only the one step and there is nothing to number.
    /// </summary>
    private static string? StepLabel(SqliteConnection connection, SqliteTransaction transaction,
        string installId, string jobId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.position, s.app_name, (SELECT COUNT(*) FROM install_steps WHERE install_id = @install)
            FROM install_steps s WHERE s.install_id = @install AND s.external_ref = @ref;
            """;
        command.Parameters.AddWithValue("@install", installId);
        command.Parameters.AddWithValue("@ref", jobId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var count = reader.GetInt64(2);
        return count > 1 ? $"Step {reader.GetInt64(0) + 1} of {count}, {reader.GetString(1)}" : null;
    }

    /// <summary>
    /// Writes the outcome onto the step this job is, so a chain can tell a finished step from an
    /// install that is still running only because the next step has not started yet.
    /// </summary>
    private static void MirrorStep(SqliteConnection connection, SqliteTransaction transaction,
        string installId, string jobId, string state, string? detail)
    {
        var stepState = state switch
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
            UPDATE install_steps SET state = @state, detail = @detail
            WHERE install_id = @install AND external_ref = @ref;
            """;
        command.Parameters.AddWithValue("@install", installId);
        command.Parameters.AddWithValue("@ref", jobId);
        command.Parameters.AddWithValue("@state", stepState.ToString());
        command.Parameters.AddWithValue("@detail", (object?)detail ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Whether this install has a step beyond the one this job is. Inline rather than through the step
    /// store, because it runs inside a transaction that store knows nothing about.
    /// </summary>
    private static bool HasStepAfter(SqliteConnection connection, SqliteTransaction transaction, string installId, string jobId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM install_steps
                WHERE install_id = @install
                  AND position > COALESCE((SELECT position FROM install_steps
                                           WHERE install_id = @install AND external_ref = @ref), -1));
            """;
        command.Parameters.AddWithValue("@install", installId);
        command.Parameters.AddWithValue("@ref", jobId);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static void Mirror(SqliteConnection connection, SqliteTransaction transaction, string installId,
        string state, int? percent, string? detail, DateTimeOffset now,
        string? reboot = null, InstallState? forced = null)
    {
        // An install waiting for a restart is one the job has finished and the person has not: the job
        // is succeeded and the install is still running, which is why the caller can override this.
        var installState = forced ?? state switch
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
                                reboot_state = COALESCE(@reboot, reboot_state),
                                completed_at = @completed, last_checked_at = @now
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("@id", installId);
        command.Parameters.AddWithValue("@state", installState.ToString());
        command.Parameters.AddWithValue("@percent", state == "succeeded" ? 100 : (object?)percent ?? DBNull.Value);
        command.Parameters.AddWithValue("@reboot", (object?)reboot ?? DBNull.Value);
        command.Parameters.AddWithValue("@detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("@completed", installState is InstallState.Queued or InstallState.Running ? DBNull.Value : SqlTime.From(now));
        command.Parameters.AddWithValue("@now", SqlTime.From(now));
        command.ExecuteNonQuery();
    }
}
