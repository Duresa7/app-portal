using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Data;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class ConcurrencyAndDurabilityTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestDatabase _test = new();

    [Fact]
    public async Task Overlapping_requests_for_the_same_app_do_not_both_start_an_install()
    {
        var catalogPath = Path.Combine(_test.Root, "catalog.json");
        File.WriteAllText(catalogPath, """
        { "apps": [ { "id": "chrome", "name": "Google Chrome", "publisher": "Google", "description": "Browser", "category": "Browsers",
            "action1": { "packageId": "Google_Google_Chrome_1570243626751_builtin", "version": "latest" } } ] }
        """);
        var token = new DeviceStore(_test.Database).Add("TESTPC", "endpoint-1234");

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:CatalogPath", catalogPath);
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 6)
            .Select(_ => client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chrome"), Json)));

        var accepted = attempts.Count(r => r.StatusCode == HttpStatusCode.Accepted);
        var conflicts = attempts.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, accepted);
        Assert.Equal(5, conflicts);

        var history = await client.GetFromJsonAsync<List<InstallRequest>>($"{ApiRoutes.Installs}?refresh=false", Json);
        Assert.Single(history!);
    }

    [Fact]
    public void A_stale_refresh_cannot_push_a_finished_install_back_to_running()
    {
        new DeviceStore(_test.Database).Add("TESTPC", "endpoint-1234");
        var store = new InstallStore(_test.Database);
        var started = DateTimeOffset.UtcNow;
        var record = new InstallRecord
        {
            Id = "abc",
            DeviceName = "TESTPC",
            AppId = "chrome",
            AppName = "Google Chrome",
            RequestedAt = started,
            State = InstallState.Running,
            PercentComplete = 40,
            LastCheckedAt = started,
        };
        store.Upsert(record);

        // The fast call lands first and finishes the install.
        var fast = store.Find("abc")!;
        fast.State = InstallState.Succeeded;
        fast.PercentComplete = 100;
        fast.CompletedAt = started.AddSeconds(2);
        fast.LastCheckedAt = started.AddSeconds(2);
        Assert.True(store.Upsert(fast));

        // The slow call started earlier and returns afterwards, carrying an older view.
        var slow = record;
        slow.State = InstallState.Running;
        slow.PercentComplete = 55;
        slow.LastCheckedAt = started.AddSeconds(1);
        Assert.False(store.Upsert(slow));

        var stored = store.Find("abc")!;
        Assert.Equal(InstallState.Succeeded, stored.State);
        Assert.Equal(100, stored.PercentComplete);
        Assert.NotNull(stored.CompletedAt);
    }

    [Fact]
    public void An_install_survives_a_restart_with_its_device_and_timestamps()
    {
        new DeviceStore(_test.Database).Add("TESTPC", "endpoint-1234");
        var requested = DateTimeOffset.UtcNow.AddMinutes(-5);
        new InstallStore(_test.Database).Upsert(new InstallRecord
        {
            Id = "abc",
            DeviceName = "TESTPC",
            AppId = "chrome",
            AppName = "Google Chrome",
            RequestedAt = requested,
            State = InstallState.Running,
            PercentComplete = 40,
            AutomationId = "automation-1",
        });

        // A second Database over the same file stands in for the next run of the server.
        var reopened = new InstallStore(new Database(_test.Database.Path));
        var stored = reopened.Find("abc")!;

        Assert.Equal("TESTPC", stored.DeviceName);
        Assert.Equal("endpoint-1234", stored.EndpointId);
        Assert.Equal("automation-1", stored.AutomationId);
        Assert.Equal(requested, stored.RequestedAt, TimeSpan.FromMilliseconds(1));
        Assert.Single(reopened.ForDevice("testpc"));
        Assert.Empty(reopened.ForDevice("OTHERPC"));
    }

    [Fact]
    public void A_database_that_is_not_a_database_stops_the_server_rather_than_serving_nothing()
    {
        var path = Path.Combine(_test.Root, "corrupt", AppPortal.Server.Data.Database.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "this is not a SQLite file");

        var database = new Database(path);

        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(database.Migrate);
    }

    [Fact]
    public void A_data_directory_that_cannot_be_created_fails_where_the_start_up_guard_sees_it()
    {
        // An unwritable or unmounted data volume refuses in the constructor, before any migration runs.
        // Program.cs therefore resolves the service inside the same try that logs and exits non-zero;
        // resolved outside it, this escapes as an unhandled exception with nothing written to the log.
        var blocker = Path.Combine(_test.Root, "not-a-directory");
        File.WriteAllText(blocker, "a file standing where the data directory should be");

        Assert.ThrowsAny<IOException>(
            () => new Database(Path.Combine(blocker, "data", AppPortal.Server.Data.Database.FileName)));
    }

    [Fact]
    public void Migrations_are_applied_once_and_running_them_again_changes_nothing()
    {
        var path = Path.Combine(_test.Root, "twice", AppPortal.Server.Data.Database.FileName);
        var database = new Database(path);
        database.Migrate();
        new DeviceStore(database).Add("PC1", "ep-1");

        database.Migrate();
        new Database(path).Migrate();

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_version;";
        Assert.Equal(1, Convert.ToInt32(command.ExecuteScalar()));
        Assert.Single(new DeviceStore(database).All());
    }

    public void Dispose() => _test.Dispose();
}
