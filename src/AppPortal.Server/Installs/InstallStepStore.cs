using AppPortal.Server.Data;
using AppPortal.Shared;

namespace AppPortal.Server.Installs;

/// <summary>One thing that has to happen for an install to be finished, and how it went.</summary>
public sealed class InstallStep
{
    public string InstallId { get; set; } = "";
    public int Position { get; set; }
    public string AppId { get; set; } = "";
    public string AppName { get; set; } = "";
    public string Engine { get; set; } = EngineLabel.Action1;
    public string? ExternalRef { get; set; }
    public InstallState State { get; set; } = InstallState.Queued;
    public string? Detail { get; set; }

    public bool IsActive => State is InstallState.Queued or InstallState.Running;
}

/// <summary>
/// The steps of an install. An install that needs nothing first has exactly one, so the chain is one
/// code path rather than two: a special case for the ordinary install is a special case that rots.
/// </summary>
public sealed class InstallStepStore(Database database)
{
    public void Add(IReadOnlyList<InstallStep> steps)
    {
        if (steps.Count == 0)
        {
            return;
        }

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var step in steps)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO install_steps (install_id, position, app_id, app_name, engine, external_ref, state, detail)
                VALUES (@install, @position, @appId, @appName, @engine, @ref, @state, @detail)
                ON CONFLICT(install_id, position) DO UPDATE SET
                    external_ref = excluded.external_ref, state = excluded.state, detail = excluded.detail;
                """;
            Bind(command, step);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public void Update(InstallStep step) => Add([step]);

    public IReadOnlyList<InstallStep> For(string installId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT install_id, position, app_id, app_name, engine, external_ref, state, detail
            FROM install_steps WHERE install_id = @install ORDER BY position;
            """;
        command.Parameters.AddWithValue("@install", installId);
        var steps = new List<InstallStep>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            steps.Add(new InstallStep
            {
                InstallId = reader.GetString(0),
                Position = (int)reader.GetInt64(1),
                AppId = reader.GetString(2),
                AppName = reader.GetString(3),
                Engine = reader.GetString(4),
                ExternalRef = reader.IsDBNull(5) ? null : reader.GetString(5),
                State = Enum.TryParse<InstallState>(reader.GetString(6), out var state) ? state : InstallState.Queued,
                Detail = reader.IsDBNull(7) ? null : reader.GetString(7),
            });
        }

        return steps;
    }

    /// <summary>
    /// Whether this install has a step after the one running. Asked by the job store, which must not
    /// call an install finished when its last step is not the step that just finished.
    /// </summary>
    public bool HasStepAfter(string installId, string? externalRef)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM install_steps
                WHERE install_id = @install
                  AND position > COALESCE((SELECT position FROM install_steps
                                           WHERE install_id = @install AND external_ref = @ref), -1));
            """;
        command.Parameters.AddWithValue("@install", installId);
        command.Parameters.AddWithValue("@ref", (object?)externalRef ?? DBNull.Value);
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static void Bind(Microsoft.Data.Sqlite.SqliteCommand command, InstallStep step)
    {
        command.Parameters.AddWithValue("@install", step.InstallId);
        command.Parameters.AddWithValue("@position", step.Position);
        command.Parameters.AddWithValue("@appId", step.AppId);
        command.Parameters.AddWithValue("@appName", step.AppName);
        command.Parameters.AddWithValue("@engine", step.Engine);
        command.Parameters.AddWithValue("@ref", (object?)step.ExternalRef ?? DBNull.Value);
        command.Parameters.AddWithValue("@state", step.State.ToString());
        command.Parameters.AddWithValue("@detail", (object?)step.Detail ?? DBNull.Value);
    }
}
