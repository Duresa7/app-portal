using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Data;
using AppPortal.Server.Devices;
using AppPortal.Server.Requests;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

public sealed class RequestHistoryMigrationTests
{
    [Fact]
    public void Existing_requests_survive_upgrade_and_device_removal()
    {
        using var test = new TestDatabase();
        var database = new Database(Path.Combine(test.Root, "before-007.db"));
        var assembly = typeof(Database).Assembly;
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);";
            command.ExecuteNonQuery();
            foreach (var resource in assembly.GetManifestResourceNames()
                         .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal))
                         .OrderBy(name => name, StringComparer.Ordinal))
            {
                var name = resource.Split(".Migrations.")[1];
                var version = int.Parse(name.Split('-')[0], System.Globalization.CultureInfo.InvariantCulture);
                if (version >= 7)
                {
                    continue;
                }

                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO schema_version VALUES (@version, @at);";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("@version", version);
                command.Parameters.AddWithValue("@at", SqlTime.Now());
                command.ExecuteNonQuery();
            }

            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO devices (id, name, token_hash, created_at)
                VALUES ('legacy-device', 'OLD-PC', 'hash', '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO app_requests (id, device_id, requested_by, text, status, reason, decided_by, decided_at, created_at)
                VALUES ('legacy-request', 'legacy-device', 'alice', 'Drawing app', 'approved', 'For design work', 'admin',
                        '2026-01-02T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        database.Migrate();
        Assert.True(new DeviceStore(database).Remove("OLD-PC"));
        var request = Assert.Single(new AppRequestStore(database).List(RequestFilter.Everything, ListQuery.All).Rows);
        Assert.Equal("OLD-PC", request.DeviceName);
        Assert.Equal("alice", request.RequestedBy);
        Assert.Equal("Drawing app", request.Text);
        Assert.Equal(AppRequestStatus.Approved, request.Status);
        Assert.Equal("For design work", request.Reason);
        Assert.Equal("admin", request.DecidedBy);
        Assert.Equal(SqlTime.Parse("2026-01-02T00:00:00.0000000+00:00"), request.DecidedAt);
        Assert.Equal(SqlTime.Parse("2026-01-01T00:00:00.0000000+00:00"), request.CreatedAt);
    }

    [Fact]
    public void Requests_at_021_upgrade_unchanged_and_unlinked()
    {
        using var test = new TestDatabase();
        var database = new Database(Path.Combine(test.Root, "before-022.db"));
        using (var connection = database.Open())
        using (var command = connection.CreateCommand())
        {
            ApplyBelow(command, 22);
            command.CommandText = """
                INSERT INTO devices (id, name, token_hash, created_at)
                VALUES ('device-1', 'PC-1', 'hash', '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO catalog_apps (id, name, created_at, updated_at)
                VALUES ('slack', 'Slack', '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                INSERT INTO app_requests (id, device_id, device_name, requested_by, text, status, reason, decided_by, decided_at, created_at)
                VALUES
                    ('approved', 'device-1', 'PC-1', 'alice', 'Slack', 'approved', 'For support', 'admin',
                     '2026-01-02T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00'),
                    ('denied', 'device-1', 'PC-1', 'bob', 'A game', 'denied', 'Not for work', 'admin',
                     '2026-01-03T00:00:00.0000000+00:00', '2026-01-01T01:00:00.0000000+00:00'),
                    ('pending', 'device-1', 'PC-1', NULL, 'Blender', 'pending', NULL, NULL, NULL,
                     '2026-01-01T02:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        database.Migrate();

        var requests = new AppRequestStore(database).List(RequestFilter.Everything, ListQuery.All).Rows
            .ToDictionary(r => r.Id);
        Assert.Equal(3, requests.Count);

        var approved = requests["approved"];
        Assert.Equal(("PC-1", "alice", "Slack", AppRequestStatus.Approved, "For support", "admin"),
            (approved.DeviceName, approved.RequestedBy, approved.Text, approved.Status, approved.Reason, approved.DecidedBy));
        Assert.Equal(SqlTime.Parse("2026-01-02T00:00:00.0000000+00:00"), approved.DecidedAt);

        var denied = requests["denied"];
        Assert.Equal(("bob", "A game", AppRequestStatus.Denied, "Not for work"), (denied.RequestedBy, denied.Text, denied.Status, denied.Reason));

        var pending = requests["pending"];
        Assert.Equal(("Blender", AppRequestStatus.Pending), (pending.Text, pending.Status));
        Assert.Null(pending.DecidedAt);

        // The catalog holds an app whose name matches a request, and still nothing is linked: 022
        // guesses no links.
        Assert.All(requests.Values, r =>
        {
            Assert.Null(r.CatalogAppId);
            Assert.Null(r.CatalogAppName);
            Assert.False(r.CatalogAppHidden);
            Assert.Null(r.ToPublic().CatalogAppId);
        });
    }

    /// <summary>Builds the schema as it stood before <paramref name="version"/>, the way the server used to.</summary>
    private static void ApplyBelow(Microsoft.Data.Sqlite.SqliteCommand command, int version)
    {
        var assembly = typeof(Database).Assembly;
        command.CommandText = "CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);";
        command.ExecuteNonQuery();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            var name = resource.Split(".Migrations.")[1];
            var number = int.Parse(name.Split('-')[0], System.Globalization.CultureInfo.InvariantCulture);
            if (number >= version)
            {
                continue;
            }

            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            command.CommandText = reader.ReadToEnd();
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO schema_version VALUES (@version, @at);";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@version", number);
            command.Parameters.AddWithValue("@at", SqlTime.Now());
            command.ExecuteNonQuery();
            command.Parameters.Clear();
        }
    }
}
