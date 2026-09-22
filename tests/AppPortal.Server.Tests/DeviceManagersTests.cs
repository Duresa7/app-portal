using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using AppPortal.Server.Devices;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class DeviceManagersTests : IDisposable
{
    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DeviceStore _devices;
    private readonly DeviceManagerStore _managers;
    private readonly string _token;
    private readonly DeviceRecord _device;

    public DeviceManagersTests()
    {
        _devices = new DeviceStore(_test.Database);
        _token = _devices.Add("AGENT-PC", "endpoint-1");
        _device = _devices.FindByName("AGENT-PC")!;
        _managers = new DeviceManagerStore(_test.Database);
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
    }

    [Fact]
    public async Task A_report_replaces_the_devices_whole_list()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/agent/managers",
            new[] { new DeviceManager("choco", "2.3.0"), new DeviceManager("scoop", "", @"CORP\ada") })).StatusCode);
        Assert.Equal(2, _managers.ForDevice(_device.Id).Count);

        // Chocolatey was uninstalled. A merge could never say so.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/agent/managers",
            new[] { new DeviceManager("scoop", "", @"CORP\ada") })).StatusCode);
        Assert.Equal(new DeviceManager("scoop", "", @"CORP\ada"), Assert.Single(_managers.ForDevice(_device.Id)));
    }

    [Fact]
    public async Task The_route_needs_a_device_token()
    {
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/agent/managers",
            new[] { new DeviceManager("choco", "2.3.0") })).StatusCode);
        Assert.Empty(_managers.ForDevice(_device.Id));
    }

    [Fact]
    public void A_manager_this_server_has_never_heard_of_is_not_kept()
    {
        // A newer agent may know a manager this server does not. Nobody could choose it on the catalog
        // page, so a row for it would only be something to explain.
        _managers.Replace(_device.Id, [new DeviceManager("apt", "2.7"), new DeviceManager("choco", "2.3.0")]);
        Assert.Equal("choco", Assert.Single(_managers.ForDevice(_device.Id)).Name);
    }

    [Fact]
    public void The_same_manager_twice_is_one_row_and_a_long_version_is_cut()
    {
        _managers.Replace(_device.Id, [new DeviceManager("npm", new string('9', 500)), new DeviceManager("npm", "10.8.2")]);
        var npm = Assert.Single(_managers.ForDevice(_device.Id));
        Assert.Equal(DeviceManagerStore.MaxVersionLength, npm.Version.Length);
    }

    [Fact]
    public void A_device_counts_once_however_many_profiles_carry_the_manager()
    {
        var other = _devices.Add("OTHER-PC", "endpoint-2");
        var otherId = _devices.FindByName("OTHER-PC")!.Id;
        _managers.Replace(_device.Id, [new DeviceManager("scoop", "", @"CORP\ada"), new DeviceManager("scoop", "", @"CORP\bob")]);
        _managers.Replace(otherId, [new DeviceManager("scoop", "0.5.2")]);

        Assert.Equal(2, _managers.DevicesByManager()["scoop"]);
        Assert.NotNull(other);
    }

    [Fact]
    public void Removing_a_device_removes_its_managers()
    {
        _managers.Replace(_device.Id, [new DeviceManager("choco", "2.3.0")]);
        Assert.True(_devices.Remove("AGENT-PC"));
        Assert.Empty(_managers.DevicesByManager());
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
