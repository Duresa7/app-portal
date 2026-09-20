using AppPortal.Server.Data;

namespace AppPortal.Server.Catalog;

/// <summary>Which apps must be installed before which, in the order an administrator put them.</summary>
public sealed class PrerequisiteStore(Database database)
{
    /// <summary>Every edge in the catalog, as app id to the ids it needs, in order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> All()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT app_id, requires_app_id FROM catalog_prerequisites ORDER BY app_id, position;";
        var edges = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var app = reader.GetString(0);
            if (!edges.TryGetValue(app, out var needs))
            {
                edges[app] = needs = [];
            }

            needs.Add(reader.GetString(1));
        }

        return edges.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> For(string appId)
        => All().TryGetValue(appId, out var needs) ? needs : [];

    /// <summary>
    /// Replaces what this app needs. The order is the order given, because an administrator who lists
    /// a runtime before a launcher means that, and nothing else in the system can know it.
    /// </summary>
    public void Replace(string appId, IReadOnlyList<string> needs)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM catalog_prerequisites WHERE app_id = @app;";
            clear.Parameters.AddWithValue("@app", appId);
            clear.ExecuteNonQuery();
        }

        var position = 0;
        foreach (var required in needs.Where(id => !string.IsNullOrWhiteSpace(id))
                     .Select(id => id.Trim())
                     .Where(id => !string.Equals(id, appId, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO catalog_prerequisites (app_id, requires_app_id, position) VALUES (@app, @requires, @position);
                """;
            insert.Parameters.AddWithValue("@app", appId);
            insert.Parameters.AddWithValue("@requires", required);
            insert.Parameters.AddWithValue("@position", position++);
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
