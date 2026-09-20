using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

using AppPortal.Server.Admin;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class AdminDevicesPageTests : IDisposable
{
    private const string Password = "a-long-enough-password";

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DeviceStore _devices;
    private readonly InstallStore _installs;
    private readonly string _token;

    public AdminDevicesPageTests()
    {
        var catalogPath = Path.Combine(_test.Root, "catalog.json");
        File.WriteAllText(catalogPath, """{ "apps": [] }""");
        _devices = new DeviceStore(_test.Database);
        _installs = new InstallStore(_test.Database);
        _token = _devices.Add("TESTPC", "endpoint-1234");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:CatalogPath", catalogPath);
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });

        new AdminStore(_test.Database).Add("admin", Password);
    }

    private async Task<HttpClient> SignedIn()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var html = await client.GetStringAsync("/admin/login");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "admin",
            ["Password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private HttpClient Device(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<string> TokenOn(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"The form on {path} carried no antiforgery token.");
        return match.Groups[1].Value;
    }

    private async Task<HttpResponseMessage> Post(HttpClient admin, string path, string formPath, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = await TokenOn(admin, formPath);
        return await admin.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    private void SeedInstall(string device, string appId, InstallState state)
        => Assert.True(_installs.Upsert(new InstallRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            DeviceName = device,
            AppId = appId,
            AppName = appId.ToUpperInvariant(),
            State = state,
            RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            CompletedAt = state is InstallState.Queued or InstallState.Running ? null : DateTimeOffset.UtcNow.AddMinutes(-9),
        }));

    [Fact]
    public async Task A_disabled_device_is_refused_by_the_api_on_its_very_next_call()
    {
        Assert.Equal(HttpStatusCode.OK, (await Device(_token).GetAsync(ApiRoutes.Catalog)).StatusCode);

        var device = _devices.FindByName("TESTPC")!;
        var admin = await SignedIn();
        var response = await Post(admin, $"/admin/devices/{device.Id}?handler=Save", $"/admin/devices/{device.Id}", new()
        {
            ["Name"] = "TESTPC",
            ["EndpointId"] = "endpoint-1234",
            ["EnginePreference"] = "",
            // Enabled is absent, which is how an unchecked box arrives.
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Device(_token).GetAsync(ApiRoutes.Catalog)).StatusCode);
    }

    [Fact]
    public async Task Rotating_the_token_stops_the_old_one_and_starts_the_new_one()
    {
        var device = _devices.FindByName("TESTPC")!;
        var admin = await SignedIn();

        var response = await Post(admin, $"/admin/devices/{device.Id}?handler=Rotate", $"/admin/devices/{device.Id}", new());

        var html = await response.Content.ReadAsStringAsync();
        var issued = Regex.Match(html, @"<code class=""secret"">(apd_[^<]+)</code>");
        Assert.True(issued.Success, "The page did not show the new token once.");
        var fresh = issued.Groups[1].Value;

        Assert.NotEqual(_token, fresh);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Device(_token).GetAsync(ApiRoutes.Catalog)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Device(fresh).GetAsync(ApiRoutes.Catalog)).StatusCode);
    }

    [Fact]
    public async Task Removing_a_device_leaves_its_installs_on_the_history_with_the_device_name()
    {
        SeedInstall("TESTPC", "chrome", InstallState.Succeeded);
        var device = _devices.FindByName("TESTPC")!;
        var admin = await SignedIn();

        var response = await Post(admin, $"/admin/devices/{device.Id}?handler=Remove", $"/admin/devices/{device.Id}", new());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Null(_devices.FindByName("TESTPC"));

        // The history outlives the device and still says which machine it happened on.
        var remaining = Assert.Single(_installs.All());
        Assert.Equal("TESTPC", remaining.DeviceName);
        Assert.Equal("chrome", remaining.AppId);

        // Follow the redirect: the list has to survive an install that now belongs to no device.
        var list = await admin.GetAsync(response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Contains("No devices match", await list.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_device_with_an_install_still_running_is_not_removed()
    {
        SeedInstall("TESTPC", "chrome", InstallState.Running);
        var device = _devices.FindByName("TESTPC")!;
        var admin = await SignedIn();

        var response = await Post(admin, $"/admin/devices/{device.Id}?handler=Remove", $"/admin/devices/{device.Id}", new());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("still running", await response.Content.ReadAsStringAsync());
        Assert.NotNull(_devices.FindByName("TESTPC"));
    }

    [Fact]
    public async Task Renaming_a_device_carries_its_history_along()
    {
        SeedInstall("TESTPC", "chrome", InstallState.Succeeded);
        var device = _devices.FindByName("TESTPC")!;
        var admin = await SignedIn();

        await Post(admin, $"/admin/devices/{device.Id}?handler=Save", $"/admin/devices/{device.Id}", new()
        {
            ["Name"] = "RENAMED-PC",
            ["EndpointId"] = "endpoint-1234",
            ["Enabled"] = "true",
            ["EnginePreference"] = "",
        });

        Assert.Equal("RENAMED-PC", Assert.Single(_installs.All()).DeviceName);
        Assert.Equal("RENAMED-PC", _devices.Find(device.Id)!.Name);
    }

    [Fact]
    public async Task A_rename_onto_another_device_is_refused()
    {
        _devices.Add("OTHERPC", "endpoint-9999");
        var device = _devices.FindByName("TESTPC")!;
        var admin = await SignedIn();

        var response = await Post(admin, $"/admin/devices/{device.Id}?handler=Save", $"/admin/devices/{device.Id}", new()
        {
            ["Name"] = "otherpc",
            ["EndpointId"] = "",
            ["Enabled"] = "true",
            ["EnginePreference"] = "",
        });

        Assert.Contains("already called", await response.Content.ReadAsStringAsync());
        Assert.Equal("TESTPC", _devices.Find(device.Id)!.Name);
    }

    [Fact]
    public async Task The_list_shows_engines_last_seen_and_the_install_count()
    {
        SeedInstall("TESTPC", "chrome", InstallState.Succeeded);
        await Device(_token).GetAsync(ApiRoutes.Catalog);
        var admin = await SignedIn();

        var html = await admin.GetStringAsync("/admin/devices");

        Assert.Contains("TESTPC", html);
        Assert.Contains(">Action1<", html);
        Assert.Contains(">Enabled<", html);
        Assert.DoesNotContain(">never<", html);
    }

    [Fact]
    public async Task Searching_narrows_the_list()
    {
        _devices.Add("OTHERPC", "endpoint-9999");
        var admin = await SignedIn();

        var html = await admin.GetStringAsync("/admin/devices?Search=other");

        Assert.Contains("OTHERPC", html);
        Assert.DoesNotContain(">TESTPC<", html);
    }

    [Fact]
    public async Task Adding_a_device_shows_its_token_once_and_the_token_works()
    {
        var admin = await SignedIn();

        var response = await Post(admin, "/admin/devices?handler=Add", "/admin/devices", new()
        {
            ["NewName"] = "NEWPC",
            ["NewEndpointId"] = "endpoint-5555",
        });

        var html = await response.Content.ReadAsStringAsync();
        var issued = Regex.Match(html, @"<code class=""secret"">(apd_[^<]+)</code>");
        Assert.True(issued.Success, "The page did not show the token once.");
        Assert.Equal(HttpStatusCode.OK, (await Device(issued.Groups[1].Value).GetAsync(ApiRoutes.Catalog)).StatusCode);

        // Coming back to the page must not show it again.
        Assert.DoesNotContain("class=\"secret\"", await admin.GetStringAsync("/admin/devices"));
    }

    [Fact]
    public async Task Adding_a_device_that_already_exists_is_refused_rather_than_reissuing()
    {
        var admin = await SignedIn();

        var response = await Post(admin, "/admin/devices?handler=Add", "/admin/devices", new()
        {
            ["NewName"] = "testpc",
            ["NewEndpointId"] = "",
        });

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("already registered", html);
        Assert.DoesNotContain("class=\"secret\"", html);
        // The existing token is untouched.
        Assert.Equal(HttpStatusCode.OK, (await Device(_token).GetAsync(ApiRoutes.Catalog)).StatusCode);
    }

    [Fact]
    public async Task The_device_pages_are_closed_without_a_session()
    {
        var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var path in new[] { "/admin/devices", "/admin/devices/anything" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/admin/login", response.Headers.Location?.OriginalString);
        }
    }

    [Fact]
    public async Task An_unknown_device_is_not_found()
    {
        var admin = await SignedIn();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/admin/devices/nope")).StatusCode);
    }

    [Fact]
    public async Task Calling_the_api_records_when_the_device_was_last_seen()
    {
        Assert.Null(_devices.FindByName("TESTPC")!.LastSeenAt);

        await Device(_token).GetAsync(ApiRoutes.Catalog);

        var seen = _devices.FindByName("TESTPC")!.LastSeenAt;
        Assert.NotNull(seen);
        Assert.True(DateTimeOffset.UtcNow - seen < TimeSpan.FromMinutes(1), "Last seen was not written as now.");
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
