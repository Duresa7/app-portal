using AppPortal.Server.Data;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.Data.Sqlite;

namespace AppPortal.Server.Tests;

public sealed class DeviceStoreTests
{
    [Fact]
    public void Token_is_stored_hashed_and_authenticates_once_issued()
    {
        using var test = new TestDatabase();
        var store = new DeviceStore(test.Database);

        var token = store.Add("PC1", "ep-1");

        Assert.StartsWith("apd_", token);
        var stored = store.All().Single();
        Assert.Equal(DeviceStore.Hash(token), stored.TokenSha256);
        Assert.NotEqual(token, stored.TokenSha256);
        Assert.NotEmpty(stored.Id);

        var device = store.Authenticate(token);
        Assert.NotNull(device);
        Assert.Equal("ep-1", device.EndpointId);
        Assert.Null(store.Authenticate(token + "x"));
        Assert.Null(store.Authenticate(""));

        Assert.True(store.Remove("PC1"));
        Assert.Null(store.Authenticate(token));
        Assert.False(store.Remove("PC1"));
    }

    [Fact]
    public void The_plaintext_token_is_nowhere_in_the_database_file()
    {
        using var test = new TestDatabase();
        var token = new DeviceStore(test.Database).Add("PC1", "ep-1");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(test.Database.Path);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.Contains(DeviceStore.Hash(token), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_a_device_again_rotates_its_token_and_keeps_one_row()
    {
        using var test = new TestDatabase();
        var store = new DeviceStore(test.Database);
        var first = store.Add("PC1", "ep-1");
        var second = store.Add("pc1", "ep-1");

        Assert.NotEqual(first, second);
        Assert.Null(store.Authenticate(first));
        Assert.NotNull(store.Authenticate(second));
        Assert.Single(store.All());
    }

    [Fact]
    public void A_device_with_settled_history_is_removed_and_the_history_stays()
    {
        using var test = new TestDatabase();
        var store = new DeviceStore(test.Database);
        store.Add("PC1", "ep-1");
        var installs = new InstallStore(test.Database);
        installs.Upsert(new InstallRecord
        {
            Id = "i1",
            DeviceName = "PC1",
            AppId = "chrome",
            AppName = "Google Chrome",
            RequestedAt = DateTimeOffset.UtcNow,
            State = InstallState.Succeeded,
        });

        // Finished history no longer blocks a removal: since migration 006 it carries the device name
        // itself, so it reads properly with no device row behind it.
        Assert.True(store.Remove("PC1"));

        Assert.Empty(store.All());
        var kept = Assert.Single(installs.All());
        Assert.Equal("PC1", kept.DeviceName);
        Assert.Equal("chrome", kept.AppId);
    }

    /// <summary>
    /// The upgrade path, which a database created by the tests never walks: every fresh database gets
    /// migration 006 before it holds an install, so only a server upgraded from an older release ever
    /// has rows that need the device name filled in behind them.
    /// </summary>
    [Fact]
    public void Migration_006_names_the_device_on_installs_that_were_already_there()
    {
        var root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "upgrade.db");
        try
        {
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString()))
            {
                connection.Open();
                using var setup = connection.CreateCommand();
                // The schema as it stood before this package, and a database that is already in use.
                setup.CommandText = MigrationSql("001-initial.sql") + MigrationSql("002-admins.sql") + """
                    CREATE TABLE schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);
                    INSERT INTO schema_version (version, applied_at) VALUES (1, '2026-01-01T00:00:00.0000000+00:00'),
                                                                           (2, '2026-01-01T00:00:00.0000000+00:00');
                    INSERT INTO devices (id, name, token_hash, enabled, action1_endpoint_id, has_agent, created_at)
                    VALUES ('d1', 'OLDPC', 'h1', 1, 'ep-1', 0, '2026-01-01T00:00:00.0000000+00:00');
                    INSERT INTO installs (id, device_id, app_id, app_name, state, percent, requested_at, last_checked_at)
                    VALUES ('i1', 'd1', 'chrome', 'Google Chrome', 'Succeeded', 100,
                            '2026-01-01T00:00:00.0000000+00:00', '2026-01-01T00:00:00.0000000+00:00');
                    """;
                setup.ExecuteNonQuery();
            }

            var database = new Database(path);
            database.Migrate();

            var devices = new DeviceStore(database);
            var installs = new InstallStore(database);

            Assert.Equal("OLDPC", Assert.Single(installs.All()).DeviceName);

            // And the row that was there before the migration outlives the device, which is the point.
            Assert.True(devices.Remove("OLDPC"));
            Assert.Equal("OLDPC", Assert.Single(installs.All()).DeviceName);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static string MigrationSql(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AppPortal.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory.FullName, "src", "AppPortal.Server", "Data", "Migrations", name)) + ";\n";
    }

    [Fact]
    public void A_device_with_an_install_still_running_cannot_be_removed()
    {
        using var test = new TestDatabase();
        var store = new DeviceStore(test.Database);
        store.Add("PC1", "ep-1");
        new InstallStore(test.Database).Upsert(new InstallRecord
        {
            Id = "i1",
            DeviceName = "PC1",
            AppId = "chrome",
            AppName = "Google Chrome",
            RequestedAt = DateTimeOffset.UtcNow,
            State = InstallState.Running,
        });

        var ex = Assert.Throws<DeviceInUseException>(() => store.Remove("PC1"));
        Assert.Contains("in progress", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(store.All());
    }
}
