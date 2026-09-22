using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AppPortal.Server.Tests;

public sealed class AdminCatalogPageTests : IDisposable
{
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
            builder.UseSetting("Catalog:MaxDownloadBytes", "32");
            builder.ConfigureServices(services => services.AddSingleton(new PackageHelpers(new HttpClient(new PackageHandler()))));
        });

        _test.AddAdmin();
    }

    private Task<HttpClient> SignedIn() => TestDatabase.SignedIn(_factory);

    private static Task<string> TokenOn(HttpClient client, string path) => TestDatabase.TokenOn(client, path);

    private HttpClient Device()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _deviceToken);
        return client;
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
    public async Task Creating_an_existing_id_does_not_overwrite_the_app()
    {
        var store = new CatalogStore(_test.Database, "");
        store.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "chrome", "name": "Google Chrome", "action1": { "packageId": "original" } } ] }
            """));
        var admin = await SignedIn();

        var response = await admin.PostAsync("/admin/catalog/new", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Id"] = " chrome ",
            ["Name"] = "Replacement",
            ["PackageId"] = "replacement",
            ["__RequestVerificationToken"] = await TokenOn(admin, "/admin/catalog/new"),
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("already exists", await response.Content.ReadAsStringAsync());
        var entry = Assert.Single(store.Entries);
        Assert.Equal("Google Chrome", entry.Name);
        Assert.Equal("original", entry.Action1.PackageId);
    }

    [Fact]
    public async Task An_invalid_new_app_keeps_its_id_editable_for_correction()
    {
        var admin = await SignedIn();

        var response = await admin.PostAsync("/admin/catalog/new", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Id"] = "chrome",
            ["Name"] = "",
            ["PackageId"] = "package",
            ["__RequestVerificationToken"] = await TokenOn(admin, "/admin/catalog/new"),
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("An app needs a name.", html);
        Assert.Contains("name=\"Id\" type=\"text\" value=\"chrome\"", html);
        Assert.Empty(new CatalogStore(_test.Database, "").Entries);
    }

    [Fact]
    public async Task Editing_an_app_preserves_its_exact_inventory_match()
    {
        var store = new CatalogStore(_test.Database, "");
        store.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "code", "name": "Visual Studio Code", "action1": { "packageId": "package" },
              "match": { "nameEquals": "Microsoft Visual Studio Code (User)" } } ] }
            """));
        var admin = await SignedIn();
        var html = await admin.GetStringAsync("/admin/catalog/code");
        var exactField = Regex.Match(html, "name=\"MatchNameEquals\"[^>]*value=\"([^\"]*)\"");
        Assert.True(exactField.Success);

        var response = await admin.PostAsync("/admin/catalog/code", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Name"] = "Code editor",
            ["PackageId"] = "package",
            ["MatchNameEquals"] = WebUtility.HtmlDecode(exactField.Groups[1].Value),
            ["__RequestVerificationToken"] = await TokenOn(admin, "/admin/catalog/code"),
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var entry = Assert.Single(store.Entries);
        Assert.Equal("Code editor", entry.Name);
        Assert.True(entry.MatchesInstalled("Microsoft Visual Studio Code (User)"));
        Assert.False(entry.MatchesInstalled("Code editor extension"));
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
    public async Task The_catalog_page_opens_for_a_signed_in_administrator()
    {
        new CatalogStore(_test.Database, "").Import(CatalogStore.Parse("""
            { "apps": [ { "id": "chrome", "name": "Google Chrome", "action1": { "packageId": "x" } } ] }
            """));

        var response = await (await SignedIn()).GetAsync("/admin/catalog?Search=goo");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Google Chrome", html);
        Assert.Contains("value=\"goo\"", html);
    }

    [Fact]
    public async Task The_catalog_pages_are_closed_without_a_session()
    {
        var anonymous = TestDatabase.Browser(_factory);

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

    [Theory]
    [InlineData("direct")]
    [InlineData("winget")]
    [InlineData("managed")]
    public async Task Agent_only_apps_can_be_created_edited_and_served_to_devices_with_an_agent(string kind)
    {
        // An app only the agent can install reaches a device only once that device has an agent. It
        // used to be served to every device, which M3-01 left for M3-05 to decide; this is that.
        var devices = new DeviceStore(_test.Database);
        devices.RecordHeartbeat(devices.FindByName("TESTPC")!.Id, "0.5.0");

        var admin = await SignedIn();
        var form = AgentForm(kind);
        form["__RequestVerificationToken"] = await TokenOn(admin, "/admin/catalog/new");
        var response = await admin.PostAsync("/admin/catalog/new", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var app = Assert.Single(await DeviceCatalog());
        Assert.Equal(["agent"], app.Engines);
        Assert.Equal(kind == "direct" ? (long?)5_000_000_000L : null, app.DownloadSizeBytes);
        var html = await admin.GetStringAsync("/admin/catalog/vendor");
        Assert.Contains(kind switch { "direct" => "5000000000", "managed" => "notepadplusplus", _ => "Valve.Steam" }, html);
        form["Name"] = "Renamed";
        response = await admin.PostAsync("/admin/catalog/vendor", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("Renamed", Assert.Single(await DeviceCatalog()).Name);
    }

    [Theory]
    [InlineData("DirectSha256", "", "sha256")]
    [InlineData("AgentKind", "unknown", "kind")]
    [InlineData("ManagedId", "notepadplusplus&calc", "not a Chocolatey package id")]
    [InlineData("ManagedId", "7zip|calc", "not a Chocolatey package id")]
    [InlineData("ManagedScope", "user", "machine scope")]
    [InlineData("ManagedManager", "apt", "must be one of")]
    [InlineData("DirectSizeBytes", "not a number", "whole number")]
    [InlineData("DirectSizeBytes", "9223372036854775808", "whole number")]
    public async Task Invalid_agent_definitions_show_a_message_without_saving(string field, string value, string message)
    {
        var admin = await SignedIn();
        var form = AgentForm(field.StartsWith("Managed", StringComparison.Ordinal) ? "managed" : "direct");
        form[field] = value;
        form["__RequestVerificationToken"] = await TokenOn(admin, "/admin/catalog/new");
        var response = await admin.PostAsync("/admin/catalog/new", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(message, await response.Content.ReadAsStringAsync());
        Assert.Empty(new CatalogStore(_test.Database, "").Entries);
    }

    [Fact]
    public async Task Fetch_failure_keeps_unsaved_form_fields_and_does_not_write_the_catalog()
    {
        var admin = await SignedIn();
        var form = AgentForm("direct");
        form["DirectUrl"] = "file:///tmp/installer.exe";
        form["__RequestVerificationToken"] = await TokenOn(admin, "/admin/catalog/new");
        var response = await admin.PostAsync("/admin/catalog/new?handler=FetchAndHash", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Could not fetch", html);
        Assert.Contains("5000000000", html);
        Assert.Contains("name=\"Id\" type=\"text\" value=\"vendor\"", html);
        Assert.Empty(new CatalogStore(_test.Database, "").Entries);
    }

    [Fact]
    public async Task Fetch_fills_the_hash_and_size_without_saving_the_form()
    {
        var admin = await SignedIn();
        var form = AgentForm("direct");
        form["__RequestVerificationToken"] = await TokenOn(admin, "/admin/catalog/new");
        var response = await admin.PostAsync("/admin/catalog/new?handler=FetchAndHash", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Hash and size filled", html);
        Assert.Contains(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("installer"u8)), html);
        Assert.Matches("name=\"DirectSizeBytes\"[^>]*value=\"9\"", html);
        Assert.Empty(new CatalogStore(_test.Database, "").Entries);
    }

    [Fact]
    public async Task Fetch_respects_the_configured_download_limit()
    {
        var admin = await SignedIn();
        var form = AgentForm("direct");
        form["DirectUrl"] = "https://vendor.example/large.exe";
        form["__RequestVerificationToken"] = await TokenOn(admin, "/admin/catalog/new");
        var response = await admin.PostAsync("/admin/catalog/new?handler=FetchAndHash", new FormUrlEncodedContent(form));
        Assert.Contains("32 byte limit", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Winget_lookup_keeps_the_form_usable_offline()
    {
        var admin = await SignedIn();
        var form = AgentForm("winget");
        form["__RequestVerificationToken"] = await TokenOn(admin, "/admin/catalog/new");
        var response = await admin.PostAsync("/admin/catalog/new?handler=LookupWinget", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("lookup is unavailable", html);
        Assert.Contains("Valve.Steam", html);
        Assert.Empty(new CatalogStore(_test.Database, "").Entries);
    }

    private sealed class PackageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                throw new HttpRequestException("Offline");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri.AbsolutePath == "/large.exe"
                    ? new ByteArrayContent(new byte[33])
                    : new ByteArrayContent("installer"u8.ToArray()),
            });
        }
    }

    private static Dictionary<string, string> AgentForm(string kind) => new()
    {
        ["Id"] = "vendor",
        ["Name"] = "Vendor app",
        ["AgentKind"] = kind,
        ["WingetId"] = "Valve.Steam",
        ["WingetScope"] = "machine",
        ["DirectUrl"] = PackageDefinitionTests.Direct.Url,
        ["DirectSha256"] = PackageDefinitionTests.Direct.Sha256,
        ["DirectInstallerType"] = "exe",
        ["DirectSilentArgs"] = "/S",
        ["DirectSizeBytes"] = "5000000000",
        ["DirectUninstallKey"] = "Vendor Application",
        ["ManagedManager"] = "choco",
        ["ManagedId"] = "notepadplusplus",
        ["ManagedScope"] = "machine",
    };

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
