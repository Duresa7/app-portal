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
}
