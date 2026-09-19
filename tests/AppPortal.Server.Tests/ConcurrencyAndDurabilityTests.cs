using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class ConcurrencyAndDurabilityTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));

    public ConcurrencyAndDurabilityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Overlapping_requests_for_the_same_app_do_not_both_start_an_install()
    {
        var catalogPath = Path.Combine(_root, "catalog.json");
        File.WriteAllText(catalogPath, """
        { "apps": [ { "id": "chrome", "name": "Google Chrome", "publisher": "Google", "description": "Browser", "category": "Browsers",
            "action1": { "packageId": "Google_Google_Chrome_1570243626751_builtin", "version": "latest" } } ] }
        """);
        var devicesPath = Path.Combine(_root, "devices.json");
        var token = new DeviceStore(devicesPath).Add("TESTPC", "endpoint-1234");

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:CatalogPath", catalogPath);
            builder.UseSetting("Portal:DevicesPath", devicesPath);
            builder.UseSetting("Portal:DataDirectory", Path.Combine(_root, "data"));
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
        var store = new InstallStore(Path.Combine(_root, "installs.json"));
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
    public void A_corrupt_device_file_does_not_throw_out_of_authenticate()
    {
        var path = Path.Combine(_root, "devices-corrupt.json");
        var store = new DeviceStore(path);
        var token = store.Add("PC1", "ep-1");
        Assert.NotNull(store.Authenticate(token));

        File.WriteAllText(path, "{ \"devices\": [ {\"Name\": \"PC1\", ");

        var exception = Record.Exception(() => store.Authenticate(token));
        Assert.Null(exception);
        Assert.Null(store.Authenticate("apd_nonsense"));
    }

    [Fact]
    public void The_device_file_is_replaced_atomically()
    {
        var path = Path.Combine(_root, "devices-atomic.json");
        var store = new DeviceStore(path);
        store.Add("PC1", "ep-1");

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Contains("PC1", File.ReadAllText(path));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
