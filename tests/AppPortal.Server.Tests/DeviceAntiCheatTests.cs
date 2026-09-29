using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using AppPortal.Server.Devices;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

/// <summary>
/// The kernel anti-cheat an agent reports. Nothing acts on it, so what matters is that the list is
/// whole, current, and the device page shows it.
/// </summary>
public sealed class DeviceAntiCheatTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DeviceStore _devices;
    private readonly DeviceAntiCheatStore _store;
    private readonly string _token;
    private readonly DeviceRecord _device;

    public DeviceAntiCheatTests()
    {
        _devices = new DeviceStore(_test.Database);
        _token = _devices.Add("GAMING-PC", "endpoint-1");
        _devices.RecordHeartbeat(_devices.FindByName("GAMING-PC")!.Id, "0.11.0");
        _device = _devices.FindByName("GAMING-PC")!;
        _store = new DeviceAntiCheatStore(_test.Database);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
        _test.AddAdmin();
    }

    private static DeviceAntiCheat Vgk(string state = "running") => new("Riot Vanguard", "vgk", "driver", state, "system");

    [Fact]
    public async Task A_report_replaces_the_devices_whole_list()
    {
        using var client = Device();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/agent/anticheat",
            new[] { Vgk(), new DeviceAntiCheat("BattlEye", "BEService", "service", "stopped", "manual") })).StatusCode);
        Assert.Equal(2, _store.ForDevice(_device.Id).Count);

        // BattlEye went with the game that brought it, and an empty list must be able to say so too.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/agent/anticheat", new[] { Vgk() })).StatusCode);
        Assert.Equal(Vgk(), Assert.Single(_store.ForDevice(_device.Id)));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/agent/anticheat", Array.Empty<DeviceAntiCheat>())).StatusCode);
        Assert.Empty(_store.ForDevice(_device.Id));
    }

    [Fact]
    public async Task The_route_needs_a_device_token()
    {
        using var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/agent/anticheat", new[] { Vgk() })).StatusCode);
        Assert.Empty(_store.ForDevice(_device.Id));
    }

    [Fact]
    public async Task A_whole_service_list_sent_by_mistake_is_refused()
    {
        using var client = Device();
        var everything = Enumerable.Range(0, 65).Select(i => new DeviceAntiCheat("x", "service" + i, "service", "running", "manual"));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/v1/agent/anticheat", everything)).StatusCode);
    }

    [Fact]
    public void Only_known_anti_cheat_is_kept_under_the_servers_own_name()
    {
        // The agent reports; the server decides what a service is called. A newer agent may know a
        // product this server does not, and a row nobody here can name is not worth showing.
        _store.Replace(_device.Id,
        [
            new DeviceAntiCheat("Something else", "EasyAntiCheat_EOS", "service", "Stopped", "Manual"),
            new DeviceAntiCheat("Unknown", "SomeOtherService", "service", "running", "automatic"),
            new DeviceAntiCheat("Easy Anti-Cheat", "easyanticheat_eos", "service", "running", "manual"),
        ]);

        var only = Assert.Single(_store.ForDevice(_device.Id));
        Assert.Equal(("Easy Anti-Cheat", "EasyAntiCheat_EOS", "stopped", "manual"), (only.Product, only.Service, only.State, only.StartType));
    }

    [Fact]
    public void Removing_a_device_removes_what_it_reported()
    {
        _store.Replace(_device.Id, [Vgk()]);

        Assert.True(_devices.Remove("GAMING-PC"));

        Assert.Empty(_store.ForDevice(_device.Id));
    }

    [Theory]
    [InlineData("stopped", "system", true)]
    [InlineData("stopped", "automatic", true)]
    [InlineData("running", "disabled", true)]
    [InlineData("stopped", "disabled", true)]
    [InlineData("stopped", "manual", false)]
    [InlineData("running", "system", false)]
    public void A_stopped_piece_that_should_start_with_windows_or_a_disabled_one_is_marked(string state, string start, bool marked)
    {
        Assert.Equal(marked, AntiCheats.NeedsAttention(new DeviceAntiCheat("Riot Vanguard", "vgk", "driver", state, start)));
    }

    [Fact]
    public async Task The_admin_api_carries_it_with_the_device()
    {
        _store.Replace(_device.Id, [Vgk("stopped")]);
        using var admin = await Admin();

        var detail = await admin.GetFromJsonAsync<AdminDeviceDetail>($"/api/v1/admin/devices/{_device.Id}", Json);

        Assert.Equal(Vgk("stopped"), Assert.Single(detail!.AntiCheats!));
    }

    [Fact]
    public async Task The_device_page_lists_it_and_marks_what_needs_a_look()
    {
        _store.Replace(_device.Id, [Vgk("stopped"), new DeviceAntiCheat("BattlEye", "BEService", "service", "stopped", "manual")]);
        using var admin = await TestDatabase.SignedIn(_factory);

        var html = await admin.GetStringAsync($"/admin/devices/{_device.Id}");

        Assert.Contains("<h2>Anti-cheat</h2>", html);
        Assert.Contains("Riot Vanguard", html);
        Assert.Contains("vgk (driver)", html);
        Assert.Contains("<strong>stopped</strong>", html);
        Assert.Contains("BEService (service)", html);
    }

    private HttpClient Device()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    private async Task<HttpClient> Admin()
    {
        var client = _factory.CreateClient();
        var issued = await client.PostAsJsonAsync("/api/v1/admin/session",
            new { username = TestDatabase.AdminUsername, password = TestDatabase.AdminPassword }, Json);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
