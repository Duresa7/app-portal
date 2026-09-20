using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using AppPortal.Server.Admin;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class AdminCatalogPageTests : IDisposable
{
    private const string Password = "a-long-enough-password";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _deviceToken;

    public AdminCatalogPageTests()
    {
        var catalogPath = Path.Combine(_test.Root, "catalog.json");
        File.WriteAllText(catalogPath, """{ "apps": [] }""");
        _deviceToken = new DeviceStore(_test.Database).Add("TESTPC", "endpoint-1234");

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

    private HttpClient Device()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _deviceToken);
        return client;
    }

    private static async Task<string> TokenOn(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"The form on {path} carried no antiforgery token.");
        return match.Groups[1].Value;
    }

    private async Task<IReadOnlyList<CatalogApp>> DeviceCatalog()
        => await Device().GetFromJsonAsync<IReadOnlyList<CatalogApp>>(ApiRoutes.Catalog, Json) ?? [];

    [Fact]
    public async Task An_app_created_in_the_browser_is_served_to_devices()
    {
        var admin = await SignedIn();
        var token = await TokenOn(admin, "/admin/catalog/new");

        var response = await admin.PostAsync("/admin/catalog/new", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Id"] = "slack",
            ["Name"] = "Slack",
            ["Publisher"] = "Slack Technologies",
            ["Description"] = "Chat.",
            ["Category"] = "Communication",
            ["PackageId"] = "Slack_Slack_123_builtin",
            ["Version"] = "latest",
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var served = await DeviceCatalog();
        var slack = Assert.Single(served);
        Assert.Equal("slack", slack.Id);
        Assert.Equal("Slack", slack.Name);
        Assert.Equal("Communication", slack.Category);
    }

    [Fact]
    public async Task A_hidden_app_disappears_from_the_device_catalog_but_stays_in_the_admin_list()
    {
        var store = new CatalogStore(_test.Database, "");
        store.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "chrome", "name": "Google Chrome", "action1": { "packageId": "x" } } ] }
            """));
        Assert.Single(await DeviceCatalog());

        var admin = await SignedIn();
        var token = await TokenOn(admin, "/admin/catalog");
        var response = await admin.PostAsync("/admin/catalog?handler=Hide", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "chrome",
            ["hidden"] = "true",
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(await DeviceCatalog());
        Assert.Contains("Google Chrome", await admin.GetStringAsync("/admin/catalog"));
    }

    [Fact]
    public async Task Deleting_an_app_with_install_history_is_refused_and_offers_hiding()
    {
        var store = new CatalogStore(_test.Database, "");
        store.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "chrome", "name": "Google Chrome", "action1": { "packageId": "x" } } ] }
            """));
        new InstallStore(_test.Database).Upsert(new InstallRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            DeviceName = "TESTPC",
            AppId = "chrome",
            AppName = "Google Chrome",
            State = InstallState.Succeeded,
            RequestedAt = DateTimeOffset.UtcNow.AddHours(-1),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-50),
        });

        var admin = await SignedIn();
        var token = await TokenOn(admin, "/admin/catalog");

        var response = await admin.PostAsync("/admin/catalog?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "chrome",
            ["__RequestVerificationToken"] = token,
        }));

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("cannot be deleted", body);
        Assert.Contains("Hide it instead", body);
        Assert.Equal(1, store.Count());
    }

    [Fact]
    public async Task An_app_with_no_history_is_deleted()
    {
        var store = new CatalogStore(_test.Database, "");
        store.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "chrome", "name": "Google Chrome", "action1": { "packageId": "x" } } ] }
            """));

        var admin = await SignedIn();
        var token = await TokenOn(admin, "/admin/catalog");

        await admin.PostAsync("/admin/catalog?handler=Delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "chrome",
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(0, store.Count());
    }

    [Fact]
    public async Task Verify_reports_what_action1_resolved_and_what_it_could_not()
    {
        var admin = await SignedIn();

        var good = await admin.GetStringAsync("/admin/catalog/new?handler=Verify&packageId=Some_Package&version=latest");
        Assert.Contains("Resolved to version 1.0.0", good);

        // The fake Action1 client resolves anything without "missing" in the id.
        var bad = await admin.GetStringAsync("/admin/catalog/new?handler=Verify&packageId=a-missing-package&version=latest");
        Assert.Contains("no such package", bad);
    }

    [Fact]
    public async Task Search_lists_repository_packages()
    {
        var admin = await SignedIn();

        var html = await admin.GetStringAsync("/admin/catalog/new?handler=Search&term=chrome");

        Assert.Contains("Google_Google_Chrome_1570243626751_builtin", html);
    }

    [Fact]
    public async Task The_catalog_pages_are_closed_without_a_session()
    {
        var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var path in new[] { "/admin/catalog", "/admin/catalog/new", "/admin/catalog/chrome" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/admin/login", response.Headers.Location?.OriginalString);
        }
    }

    [Fact]
    public async Task Export_returns_the_catalog_as_a_file_that_parses()
    {
        new CatalogStore(_test.Database, "").Import(CatalogStore.Parse("""
            { "apps": [ { "id": "chrome", "name": "Google Chrome", "action1": { "packageId": "x" } } ] }
            """));
        var admin = await SignedIn();

        var response = await admin.GetAsync("/admin/catalog?handler=Export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var parsed = CatalogStore.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("chrome", Assert.Single(parsed).Id);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
