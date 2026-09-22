using AppPortal.Server.Data;
using AppPortal.Server.Devices;
using AppPortal.Server.Enrollment;
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

        test.Database.ClearPool();
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
            Database.ClearPoolFor(path);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData("winget")]
    [InlineData("both")]
    [InlineData("inherit")]
    public void An_unknown_engine_preference_is_rejected_as_bad_input(string engine)
    {
        using var test = new TestDatabase();
        var devices = new DeviceStore(test.Database);
        devices.Add("PC1", "ep-1");
        var device = devices.All().Single();
        device.EnginePreference = engine;
        var refused = Assert.Throws<DeviceInvalidException>(() => devices.Update(device));
        Assert.Equal("The engine preference must be action1 or agent, or empty to follow the server.", refused.Message);
        Assert.Null(devices.Find(device.Id)!.EnginePreference);
    }

    [Fact]
    public void A_blank_name_is_rejected_as_bad_input()
    {
        using var test = new TestDatabase();
        var devices = new DeviceStore(test.Database);
        devices.Add("PC1", "ep-1");
        var device = devices.All().Single();
        device.Name = "  ";
        Assert.Throws<DeviceInvalidException>(() => devices.Update(device));
        Assert.Equal("PC1", devices.Find(device.Id)!.Name);
    }

    [Fact]
    public void A_name_another_device_has_is_a_conflict_not_bad_input()
    {
        using var test = new TestDatabase();
        var devices = new DeviceStore(test.Database);
        devices.Add("PC1", "ep-1");
        devices.Add("PC2", "ep-2");
        var device = devices.FindByName("PC1")!;
        device.Name = "pc2";
        var refused = Assert.Throws<DeviceRejectedException>(() => devices.Update(device));
        Assert.IsNotType<DeviceInvalidException>(refused);
    }

    [Theory]
    [InlineData(" ACTION1 ", "action1")]
    [InlineData("agent", "agent")]
    [InlineData(" ", null)]
    public void Engine_preferences_are_stored_canonically(string input, string? expected)
    {
        using var test = new TestDatabase();
        var devices = new DeviceStore(test.Database);
        devices.Add("PC1", "ep-1");
        var device = devices.All().Single();
        device.EnginePreference = input;
        devices.Update(device);
        Assert.Equal(expected, devices.Find(device.Id)!.EnginePreference);
    }

    [Fact]
    public void A_refresh_started_before_a_rename_keeps_the_install_owner_and_new_name()
    {
        using var test = new TestDatabase();
        var devices = new DeviceStore(test.Database);
        devices.Add("PC1", "ep-1");
        var device = devices.All().Single();
        var installs = new InstallStore(test.Database);
        installs.Upsert(new InstallRecord
        {
            Id = "i1",
            DeviceName = "PC1",
            AppId = "app",
            AppName = "App",
            State = InstallState.Running,
            RequestedAt = DateTimeOffset.UtcNow,
        });
        var pendingRefresh = installs.Find("i1")!;
        device.Name = "RENAMED";
        devices.Update(device);
        pendingRefresh.State = InstallState.Succeeded;
        pendingRefresh.LastCheckedAt = DateTimeOffset.UtcNow;

        Assert.True(installs.Upsert(pendingRefresh));
        var refreshed = installs.Find("i1")!;
        Assert.Equal(device.Id, refreshed.DeviceId);
        Assert.Equal("RENAMED", refreshed.DeviceName);
        Assert.Equal(InstallState.Succeeded, refreshed.State);
    }

    [Fact]
    public void Updating_a_retired_install_cannot_attach_it_to_a_replacement_device()
    {
        using var test = new TestDatabase();
        var devices = new DeviceStore(test.Database);
        devices.Add("PC1", "ep-1");
        var installs = new InstallStore(test.Database);
        installs.Upsert(new InstallRecord
        {
            Id = "i1",
            DeviceName = "PC1",
            AppId = "app",
            AppName = "App",
            State = InstallState.Succeeded,
            RequestedAt = DateTimeOffset.UtcNow,
        });
        var previous = installs.Find("i1")!;
        Assert.True(devices.Remove("PC1"));
        devices.Add("PC1", "ep-2");
        previous.Detail = "Updated detail";
        previous.LastCheckedAt = DateTimeOffset.UtcNow;

        Assert.True(installs.Upsert(previous));
        var saved = installs.Find("i1")!;
        Assert.Null(saved.DeviceId);
        Assert.Equal("PC1", saved.DeviceName);
        Assert.Equal("Updated detail", saved.Detail);
        Assert.Empty(installs.ForDeviceId(devices.FindByName("PC1")!.Id));
    }

    [Fact]
    public void A_pending_creation_cannot_attach_to_a_replacement_device_with_the_same_name()
    {
        using var test = new TestDatabase();
        var devices = new DeviceStore(test.Database);
        devices.Add("PC1", "ep-1");
        var original = devices.All().Single();
        devices.RemoveById(original.Id);
        devices.Add("PC1", "ep-2");
        var installs = new InstallStore(test.Database);

        Assert.Throws<SqliteException>(() => installs.Upsert(new InstallRecord
        {
            Id = "i1",
            DeviceId = original.Id,
            DeviceName = original.Name,
            AppId = "app",
            AppName = "App",
            State = InstallState.Queued,
            RequestedAt = DateTimeOffset.UtcNow,
        }));
        Assert.Empty(installs.All());
    }

    [Fact]
    public void The_endpoint_cannot_change_until_the_active_install_finishes()
    {
        using var test = new TestDatabase();
        var devices = new DeviceStore(test.Database);
        devices.Add("PC1", "ep-1");
        var device = devices.All().Single();
        var installs = new InstallStore(test.Database);
        var install = new InstallRecord
        {
            Id = "i1",
            DeviceName = "PC1",
            AppId = "app",
            AppName = "App",
            State = InstallState.Running,
            RequestedAt = DateTimeOffset.UtcNow,
        };
        installs.Upsert(install);
        device.EndpointId = "ep-2";
        Assert.Throws<DeviceRejectedException>(() => devices.Update(device));
        Assert.Throws<DeviceRejectedException>(() => devices.Add("PC1", "ep-2"));
        Assert.Equal("ep-1", installs.Find("i1")!.EndpointId);
        install.State = InstallState.Succeeded;
        installs.Upsert(install);
        devices.Update(device);
        Assert.Equal("ep-2", devices.Find(device.Id)!.EndpointId);
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

    [Fact]
    public void Enrolling_spends_a_use_only_when_the_device_is_written()
    {
        using var test = new TestDatabase();
        var store = new DeviceStore(test.Database);
        var keys = new EnrollmentKeyStore(test.Database);
        var key = keys.Create("One use", EnrollmentEngine.Action1, null, 1, "admin").Key;

        // A device pinned to its endpoint by an install in flight refuses to move, and the refusal
        // rolls the use back with everything else the transaction did.
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
        Assert.Throws<DeviceRejectedException>(() => store.Enroll("machine-1", "PC1", "ep-2", grantAgent: false, null, key.Id));
        Assert.Equal(0, keys.Find(key.Id)!.Uses);

        store.Enroll("machine-2", "PC2", "ep-3", grantAgent: false, null, key.Id);
        Assert.Equal(1, keys.Find(key.Id)!.Uses);

        Assert.Throws<EnrollmentKeyNotUsableException>(() => store.Enroll("machine-3", "PC3", "ep-4", grantAgent: false, null, key.Id));
        Assert.Equal(1, keys.Find(key.Id)!.Uses);
        Assert.Null(store.FindByName("PC3"));
    }

    [Fact]
    public void A_revoked_key_enrolls_nothing()
    {
        using var test = new TestDatabase();
        var store = new DeviceStore(test.Database);
        var keys = new EnrollmentKeyStore(test.Database);
        var key = keys.Create("Revoked", EnrollmentEngine.Agent, null, null, "admin").Key;
        keys.Revoke(key.Id);

        Assert.Throws<EnrollmentKeyNotUsableException>(() => store.Enroll("machine-1", "PC1", null, grantAgent: true, null, key.Id));
        Assert.Empty(store.All());
        Assert.Equal(0, keys.Find(key.Id)!.Uses);
    }

    [Fact]
    public async Task Twenty_machines_racing_for_three_uses_enroll_exactly_three()
    {
        using var test = new TestDatabase();
        var store = new DeviceStore(test.Database);
        var keys = new EnrollmentKeyStore(test.Database);
        var key = keys.Create("Three uses", EnrollmentEngine.Agent, null, 3, "admin").Key;

        // Released together, so they contend for the last uses rather than queueing behind each other.
        using var start = new ManualResetEventSlim(false);
        var winners = 0;
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            start.Wait();
            try
            {
                store.Enroll($"machine-{i}", $"PC{i}", null, grantAgent: true, null, key.Id);
                Interlocked.Increment(ref winners);
            }
            catch (EnrollmentKeyNotUsableException)
            {
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(3, winners);
        Assert.Equal(3, keys.Find(key.Id)!.Uses);
        Assert.Equal(3, store.All().Count);
    }
}
