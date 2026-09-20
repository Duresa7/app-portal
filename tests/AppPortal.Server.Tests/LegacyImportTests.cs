using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Catalog;
using AppPortal.Server.Data;
using AppPortal.Server.Devices;
using AppPortal.Server.Options;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Server.Tests;

/// <summary>
/// A 0.2.x deployment upgrading in place: its three JSON files move into the database on the first
/// start, and never again, so a stale file cannot undo a later change.
/// </summary>
public sealed class LegacyImportTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private const string LegacyToken = "apd_legacy-token-from-the-old-deployment";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
    private readonly string _dataDirectory;
    private readonly string _catalogPath;
    private readonly string _devicesPath;

    public LegacyImportTests()
    {
        _dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(_dataDirectory);
        _catalogPath = Path.Combine(_root, "catalog.json");
        _devicesPath = Path.Combine(_dataDirectory, "devices.json");

        File.WriteAllText(_catalogPath, """
        { "apps": [ { "id": "chrome", "name": "Google Chrome", "publisher": "Google", "description": "Browser",
            "category": "Browsers", "featured": true, "match": { "nameContains": "Google Chrome" },
            "action1": { "packageId": "Google_Google_Chrome_1570243626751_builtin", "version": "latest" } } ] }
        """);

        File.WriteAllText(_devicesPath, $$"""
        { "devices": [ { "name": "OLDPC", "endpointId": "endpoint-old", "tokenSha256": "{{DeviceStore.Hash(LegacyToken)}}",
            "enabled": true, "createdAt": "2026-01-02T03:04:05+00:00" } ] }
        """);

        File.WriteAllText(Path.Combine(_dataDirectory, "installs.json"), """
        [ { "id": "old-install-1", "deviceName": "OLDPC", "endpointId": "endpoint-old", "appId": "chrome",
            "appName": "Google Chrome", "packageId": "Google_Google_Chrome_1570243626751_builtin", "version": "1.2.3",
            "automationId": "automation-old", "requestedAt": "2026-01-02T03:05:00+00:00",
            "completedAt": "2026-01-02T03:07:00+00:00", "lastCheckedAt": "2026-01-02T03:07:00+00:00",
            "state": "Succeeded", "percentComplete": 100, "detail": "Installed." },
          { "id": "orphan-install", "deviceName": "GONEPC", "appId": "chrome", "appName": "Google Chrome",
            "requestedAt": "2026-01-02T03:05:00+00:00", "state": "Failed", "percentComplete": 0 } ]
        """);
    }

    [Fact]
    public async Task The_old_files_answer_the_same_questions_after_the_upgrade()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LegacyToken);

        var device = await client.GetFromJsonAsync<DeviceInfo>(ApiRoutes.Device, Json);
        Assert.Equal("OLDPC", device!.DeviceName);
        Assert.Equal("endpoint-old", device.EndpointId);

        var apps = await client.GetFromJsonAsync<List<CatalogApp>>(ApiRoutes.Catalog, Json);
        Assert.Single(apps!);
        Assert.Equal("Google Chrome", apps![0].Name);
        Assert.True(apps[0].Featured);

        var history = await client.GetFromJsonAsync<List<InstallRequest>>($"{ApiRoutes.Installs}?refresh=false", Json);
        var install = Assert.Single(history!);
        Assert.Equal("old-install-1", install.Id);
        Assert.Equal(InstallState.Succeeded, install.State);
        Assert.Equal(100, install.PercentComplete);
        Assert.NotNull(install.CompletedAt);
    }

    [Fact]
    public void An_import_happens_once_and_a_later_edit_to_the_files_is_ignored()
    {
        var database = new Database(Path.Combine(_dataDirectory, AppPortal.Server.Data.Database.FileName));
        database.Migrate();
        Import(database);

        var catalog = new CatalogStore(database, _catalogPath);
        Assert.Equal(1, catalog.Count());
        Assert.Single(new DeviceStore(database).All());

        // The operator edits the old files after the upgrade. The database is what the server serves now.
        File.WriteAllText(_catalogPath, """
        { "apps": [ { "id": "vlc", "name": "VLC", "action1": { "packageId": "VideoLAN_builtin" } } ] }
        """);
        File.WriteAllText(_devicesPath, """{ "devices": [] }""");

        Import(database);

        Assert.Equal(1, catalog.Count());
        Assert.Equal("chrome", catalog.Entries[0].Id);
        Assert.Single(new DeviceStore(database).All());
    }

    [Fact]
    public void An_install_naming_a_device_that_is_gone_is_left_behind_rather_than_stopping_the_import()
    {
        var database = new Database(Path.Combine(_dataDirectory, AppPortal.Server.Data.Database.FileName));
        database.Migrate();

        Import(database);

        var store = new AppPortal.Server.Installs.InstallStore(database);
        Assert.Single(store.All());
        Assert.Equal("old-install-1", store.All()[0].Id);
    }

    private void Import(Database database)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new PortalOptions
        {
            CatalogPath = _catalogPath,
            DevicesPath = _devicesPath,
            DataDirectory = _dataDirectory,
        });
        var env = new StubEnvironment { ContentRootPath = _root };
        new LegacyImport(database, new CatalogStore(database, _catalogPath), options, env, NullLogger<LegacyImport>.Instance).Run();
    }

    private WebApplicationFactory<Program> Factory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:CatalogPath", _catalogPath);
            builder.UseSetting("Portal:DevicesPath", _devicesPath);
            builder.UseSetting("Portal:DataDirectory", _dataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });

    public void Dispose()
    {
        Database.ClearPoolFor(Path.Combine(_dataDirectory, Database.FileName));
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class StubEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "AppPortal.Server.Tests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
