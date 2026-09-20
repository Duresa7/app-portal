using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Admin;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Enrollment;
using AppPortal.Server.Installs;
using AppPortal.Server.Requests;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

// The plan names the wire contract EnrollmentKeyCreated and the store already calls its own result the
// same thing. Both are the right name for what they hold, and only code that sees both namespaces has
// to choose; the client sees AppPortal.Shared alone.
using EnrollmentKeyCreated = AppPortal.Shared.EnrollmentKeyCreated;

namespace AppPortal.Server.Tests;

/// <summary>
/// The admin JSON API. Two things are being proved here, and the second matters more than the first.
///
/// One: every route answers the way the page it mirrors answers, because the client is about to rely
/// on that. Two: no route answers at all without an enabled administrator's live session behind it.
/// This API hands the whole fleet to whoever holds a token, so the authorisation tests are the part
/// that has to stay green: every route is checked with no credential, with a device token, with a
/// disabled administrator's token, and with a revoked one.
/// </summary>
public sealed class AdminApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json =
        new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly RecordedLogs _logs = new();
    private readonly CatalogStore _catalog;
    private readonly DeviceStore _devices;
    private readonly InstallStore _installs;
    private readonly AppRequestStore _requests;
    private readonly EnrollmentKeyStore _keys;
    private readonly AdminStore _admins;

    public AdminApiTests()
    {
        var catalogPath = Path.Combine(_test.Root, "catalog.json");
        File.WriteAllText(catalogPath, """{ "apps": [] }""");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:CatalogPath", catalogPath);
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
            builder.ConfigureLogging(logging => logging.AddProvider(new RecordingLoggerProvider(_logs)));
        });

        _test.AddAdmin();
        _catalog = new CatalogStore(_test.Database, catalogPath);
        _devices = new DeviceStore(_test.Database);
        _installs = new InstallStore(_test.Database);
        _requests = new AppRequestStore(_test.Database);
        _keys = new EnrollmentKeyStore(_test.Database);
        _admins = new AdminStore(_test.Database);
    }

    // ---- the routes, in one place -------------------------------------------------------------
    //
    // Every route the admin API exposes. The authorisation theories below run over this list, so a
    // route added without a row here is a route nobody proved is closed; adding the row is the point.

    public static TheoryData<string, string> EveryRoute()
    {
        var routes = new TheoryData<string, string>();
        foreach (var (method, path) in Routes)
        {
            routes.Add(method, path);
        }

        return routes;
    }

    private static readonly (string Method, string Path)[] Routes =
    [
        ("GET", "/api/v1/admin/sessions"),
        ("DELETE", "/api/v1/admin/sessions/any-id"),
        ("GET", "/api/v1/admin/dashboard"),
        ("GET", "/api/v1/admin/installs"),
        ("GET", "/api/v1/admin/installs/any-id"),
        ("GET", "/api/v1/admin/requests"),
        ("POST", "/api/v1/admin/requests/any-id/approve"),
        ("POST", "/api/v1/admin/requests/any-id/deny"),
        ("GET", "/api/v1/admin/catalog"),
        ("GET", "/api/v1/admin/catalog/export"),
        ("GET", "/api/v1/admin/catalog/any-id"),
        ("PUT", "/api/v1/admin/catalog/any-id"),
        ("DELETE", "/api/v1/admin/catalog/any-id"),
        ("POST", "/api/v1/admin/catalog/any-id/hidden"),
        ("POST", "/api/v1/admin/catalog/import"),
        ("POST", "/api/v1/admin/catalog/action1/search"),
        ("POST", "/api/v1/admin/catalog/action1/verify"),
        ("POST", "/api/v1/admin/catalog/package/hash"),
        ("POST", "/api/v1/admin/catalog/package/winget"),
        ("GET", "/api/v1/admin/devices"),
        ("POST", "/api/v1/admin/devices"),
        ("GET", "/api/v1/admin/devices/any-id"),
        ("PUT", "/api/v1/admin/devices/any-id"),
        ("POST", "/api/v1/admin/devices/any-id/rotate-token"),
        ("DELETE", "/api/v1/admin/devices/any-id"),
        ("GET", "/api/v1/admin/keys"),
        ("POST", "/api/v1/admin/keys"),
        ("GET", "/api/v1/admin/keys/any-id"),
        ("POST", "/api/v1/admin/keys/any-id/revoke"),
        ("GET", "/api/v1/admin/keys/any-id/events"),
        ("GET", "/api/v1/admin/admins"),
        ("POST", "/api/v1/admin/admins"),
        ("POST", "/api/v1/admin/admins/any-id/disable"),
        ("POST", "/api/v1/admin/admins/any-id/reset-password"),
        ("GET", "/api/v1/admin/settings"),
        ("PUT", "/api/v1/admin/settings"),
    ];

    // ---- authorisation ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task Every_route_is_closed_without_a_token(string method, string path)
    {
        var response = await Send(_factory.CreateClient(), method, path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A device token is the most numerous credential in the system and the easiest to lift off a PC.
    /// It authenticates one machine for its own catalog and its own installs, and must never be worth
    /// anything on a route that can rewrite the catalog or disable an administrator.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task Every_route_refuses_a_device_token(string method, string path)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _devices.Add("STOLEN-PC", "endpoint-1"));

        var response = await Send(client, method, path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(EveryRoute))]
    public async Task Every_route_answers_an_administrator(string method, string path)
    {
        var response = await Send(await Admin(), method, path);

        // Not what the route did with a made-up id, only that it let the caller in: anything but 401
        // and 403 means authorisation passed and the handler, not the policy, wrote the answer.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_disabled_administrators_token_stops_working_at_once()
    {
        _admins.Add("second", TestDatabase.AdminPassword);
        var client = await Admin("second");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/admin/dashboard")).StatusCode);

        // Not at expiry, now: the session row survives, and the account behind it is read on every call.
        _admins.SetDisabled("second", true);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/dashboard")).StatusCode);
    }

    [Fact]
    public async Task A_revoked_sessions_token_stops_working()
    {
        var client = await Admin();
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/admin/session")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/dashboard")).StatusCode);
    }

    [Fact]
    public async Task A_browser_session_token_is_not_an_api_token()
    {
        // The cookie's own value resolves against the same table under a different kind. Presenting it
        // as a bearer token must not work, or a stolen cookie would become an API credential.
        var cookieToken = new AdminSessionStore(_test.Database)
            .Create(_admins.Find(TestDatabase.AdminUsername)!.Id, AdminSessionKind.Web);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", cookieToken);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/dashboard")).StatusCode);
    }

    // ---- logging ------------------------------------------------------------------------------

    [Fact]
    public async Task Each_call_is_logged_with_the_administrator_and_the_route_and_no_body()
    {
        var client = await Admin();

        var created = await client.PostAsJsonAsync("/api/v1/admin/keys", new { name = "rollout", engine = "agent" }, Json);
        var key = await created.Content.ReadFromJsonAsync<EnrollmentKeyCreated>(Json);

        Assert.Contains(_logs.Messages, m =>
            m.Contains($"Administrator {TestDatabase.AdminUsername} called", StringComparison.Ordinal)
            && m.Contains("POST /api/v1/admin/keys", StringComparison.Ordinal));

        // The one thing that must never reach a log: an enrollment key exists in plaintext in exactly
        // one reply and nowhere else, and a log line is where a secret gets copied, shipped and kept.
        Assert.DoesNotContain(_logs.Messages, m => m.Contains(key!.Plaintext, StringComparison.Ordinal));
        Assert.DoesNotContain(_logs.Messages, m => m.Contains(TestDatabase.AdminPassword, StringComparison.Ordinal));
    }

    /// <summary>
    /// The path arrives decoded, so an id carrying a newline would write a log line of its own and let
    /// a caller forge entries in the record of who did what.
    /// </summary>
    [Fact]
    public async Task An_id_cannot_write_a_line_of_its_own_into_the_log()
    {
        await (await Admin()).GetAsync("/api/v1/admin/installs/one%0Awarn:%20nothing%20happened");

        Assert.DoesNotContain(_logs.Messages, m => m.Contains('\n') || m.Contains('\r'));
        Assert.Contains(_logs.Messages, m => m.Contains("/api/v1/admin/installs/one.warn:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_new_administrators_password_is_never_logged()
    {
        const string password = "another-long-enough-password";
        var client = await Admin();

        await client.PostAsJsonAsync("/api/v1/admin/admins", new { username = "logged", password }, Json);

        Assert.DoesNotContain(_logs.Messages, m => m.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rotated_device_token_is_never_logged()
    {
        _devices.Add("ROTATE-PC", "endpoint-2");
        var device = _devices.FindByName("ROTATE-PC")!;
        var client = await Admin();

        var rotated = await client.PostAsync($"/api/v1/admin/devices/{device.Id}/rotate-token", null);
        var token = await rotated.Content.ReadFromJsonAsync<AdminDeviceToken>(Json);

        Assert.DoesNotContain(_logs.Messages, m => m.Contains(token!.DeviceToken, StringComparison.Ordinal));
    }

    // ---- paging -------------------------------------------------------------------------------

    [Fact]
    public async Task A_limit_above_the_ceiling_is_cut_to_it()
    {
        for (var i = 0; i < AdminApiLimits.MaxLimit + 25; i++)
        {
            _devices.Add($"PC-{i:0000}", "");
        }

        var page = await (await Admin()).GetFromJsonAsync<AdminPage<AdminDevice>>("/api/v1/admin/devices?limit=100000", Json);

        Assert.Equal(AdminApiLimits.MaxLimit, page!.Limit);
        Assert.Equal(AdminApiLimits.MaxLimit, page.Items.Count);
        Assert.True(page.HasMore);
        Assert.Equal(AdminApiLimits.MaxLimit + 25, page.Total);
    }

    [Fact]
    public async Task Offset_walks_the_list_and_nonsense_paging_falls_back_to_the_default()
    {
        _devices.Add("PC-A", "");
        _devices.Add("PC-B", "");
        _devices.Add("PC-C", "");
        var client = await Admin();

        var second = await client.GetFromJsonAsync<AdminPage<AdminDevice>>("/api/v1/admin/devices?limit=1&offset=1", Json);
        Assert.Equal("PC-B", Assert.Single(second!.Items).Name);
        Assert.Equal(1, second.Offset);

        var nonsense = await client.GetFromJsonAsync<AdminPage<AdminDevice>>("/api/v1/admin/devices?limit=nope&offset=-5", Json);
        Assert.Equal(AdminApiLimits.DefaultLimit, nonsense!.Limit);
        Assert.Equal(0, nonsense.Offset);
    }

    // ---- sessions -----------------------------------------------------------------------------

    [Fact]
    public async Task A_session_carries_the_device_name_it_signed_in_with_and_can_be_revoked()
    {
        var client = await Admin(deviceName: "LAPTOP-7");

        var sessions = await client.GetFromJsonAsync<List<AdminSessionSummary>>("/api/v1/admin/sessions", Json);
        var current = Assert.Single(sessions!, s => s.Current);
        Assert.Equal("LAPTOP-7", current.DeviceName);
        Assert.Equal("api", current.Kind);

        // Thirty days, sliding, is what makes a revocable session list worth having in the first place.
        Assert.True(current.ExpiresAt - DateTimeOffset.UtcNow > TimeSpan.FromDays(29));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/admin/sessions/{current.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/admin/sessions")).StatusCode);
    }

    [Fact]
    public async Task One_administrator_cannot_revoke_another_ones_session()
    {
        _admins.Add("second", TestDatabase.AdminPassword);
        var other = await Admin("second", "OTHER-PC");
        var otherSessions = await other.GetFromJsonAsync<List<AdminSessionSummary>>("/api/v1/admin/sessions", Json);
        var target = Assert.Single(otherSessions!).Id;

        var response = await (await Admin()).DeleteAsync($"/api/v1/admin/sessions/{target}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/v1/admin/sessions")).StatusCode);
    }

    // ---- dashboard ----------------------------------------------------------------------------

    [Fact]
    public async Task The_dashboard_counts_what_the_page_counts()
    {
        _devices.Add("PC-A", "endpoint-a");
        SeedInstall("PC-A", "chrome", InstallState.Failed, DateTimeOffset.Now.AddHours(-1));
        SeedInstall("PC-A", "vlc", InstallState.Running, DateTimeOffset.Now.AddMinutes(-5));
        _requests.CreateForDeviceId(_devices.FindByName("PC-A")!.Id, @"SMOKE\operator", "A PDF editor, please.");

        var counts = await (await Admin()).GetFromJsonAsync<DashboardCounts>("/api/v1/admin/dashboard", Json);

        Assert.Equal(1, counts!.Devices);
        Assert.Equal(2, counts.InstallsToday);
        Assert.Equal(1, counts.FailuresThisWeek);
        Assert.Equal(1, counts.ActiveNow);
        Assert.Equal(1, counts.PendingRequests);
    }

    // ---- installs -----------------------------------------------------------------------------

    [Fact]
    public async Task Installs_list_with_the_filters_the_page_offers_and_read_one()
    {
        _devices.Add("PC-A", "endpoint-a");
        _devices.Add("PC-B", "endpoint-b");
        var failed = SeedInstall("PC-A", "chrome", InstallState.Failed, DateTimeOffset.Now.AddHours(-2), @"CORP\ann");
        SeedInstall("PC-B", "vlc", InstallState.Succeeded, DateTimeOffset.Now.AddHours(-1));
        var client = await Admin();

        var all = await client.GetFromJsonAsync<AdminPage<AdminInstall>>("/api/v1/admin/installs", Json);
        Assert.Equal(2, all!.Total);

        var onlyFailed = await client.GetFromJsonAsync<AdminPage<AdminInstall>>("/api/v1/admin/installs?state=Failed", Json);
        Assert.Equal("chrome", Assert.Single(onlyFailed!.Items).AppId);

        var byDevice = await client.GetFromJsonAsync<AdminPage<AdminInstall>>("/api/v1/admin/installs?device=PC-B", Json);
        Assert.Equal("vlc", Assert.Single(byDevice!.Items).AppId);

        var byRequester = await client.GetFromJsonAsync<AdminPage<AdminInstall>>(@"/api/v1/admin/installs?requester=CORP\ann", Json);
        Assert.Equal("chrome", Assert.Single(byRequester!.Items).AppId);

        var one = await client.GetFromJsonAsync<AdminInstall>($"/api/v1/admin/installs/{failed.Id}", Json);
        Assert.Equal("chrome", one!.AppId);
        Assert.Equal(InstallState.Failed, one.State);
        Assert.Equal(@"CORP\ann", one.RequestedBy);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/admin/installs/nothing")).StatusCode);
    }

    // ---- requests -----------------------------------------------------------------------------

    [Fact]
    public async Task A_request_is_approved_once_and_the_second_decision_is_refused()
    {
        var id = SeedRequest("A PDF editor, please.");
        var client = await Admin();

        var approved = await client.PostAsJsonAsync($"/api/v1/admin/requests/{id}/approve", new { reason = "Fine by me." }, Json);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var body = await approved.Content.ReadFromJsonAsync<AdminRequest>(Json);
        Assert.Equal(AppRequestStatus.Approved, body!.Status);
        Assert.Equal("Fine by me.", body.Reason);
        Assert.Equal(TestDatabase.AdminUsername, body.DecidedBy);

        var again = await client.PostAsJsonAsync($"/api/v1/admin/requests/{id}/deny", new AdminDecision(null), Json);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // The decision that was recorded stands, rather than the second caller writing over it.
        Assert.Equal(AppRequestStatus.Approved, _requests.Find(id)!.Status);
    }

    [Fact]
    public async Task A_request_is_denied_with_a_reason_the_device_can_read()
    {
        var id = SeedRequest("A game, please.");

        var denied = await (await Admin()).PostAsJsonAsync($"/api/v1/admin/requests/{id}/deny", new AdminDecision("Not licensed."), Json);

        Assert.Equal(HttpStatusCode.OK, denied.StatusCode);
        Assert.Equal(AppRequestStatus.Denied, _requests.Find(id)!.Status);
        Assert.Equal("Not licensed.", _requests.Find(id)!.Reason);
    }

    [Fact]
    public async Task Deciding_an_unknown_request_is_a_404_and_an_overlong_reason_is_a_400()
    {
        var client = await Admin();
        var id = SeedRequest("Something.");

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync("/api/v1/admin/requests/nothing/approve", new AdminDecision(null), Json)).StatusCode);

        var tooLong = new AdminDecision(new string('x', AppRequestLimits.MaxTextLength + 1));
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/v1/admin/requests/{id}/approve", tooLong, Json)).StatusCode);
        Assert.Equal(AppRequestStatus.Pending, _requests.Find(id)!.Status);
    }

    [Fact]
    public async Task Requests_list_everything_by_default_and_narrow_by_status()
    {
        var pending = SeedRequest("Still waiting.");
        var decided = SeedRequest("Already answered.");
        _requests.Decide(decided, AppRequestStatus.Approved, null, "someone");
        var client = await Admin();

        var all = await client.GetFromJsonAsync<AdminPage<AdminRequest>>("/api/v1/admin/requests", Json);
        Assert.Equal(2, all!.Total);

        var onlyPending = await client.GetFromJsonAsync<AdminPage<AdminRequest>>("/api/v1/admin/requests?status=pending", Json);
        Assert.Equal(pending, Assert.Single(onlyPending!.Items).Id);

        // A typo must not quietly narrow the list to one status, which is what the page's tab does.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/admin/requests?status=pendign")).StatusCode);
    }

    // ---- catalog ------------------------------------------------------------------------------

    [Fact]
    public async Task A_catalog_app_is_written_read_back_hidden_and_deleted()
    {
        var client = await Admin();
        var app = new AdminCatalogApp("seven-zip", "7-Zip", "Igor Pavlov", "Archiver", "Utilities",
            Action1: new AdminAction1Package("7zip_builtin", "latest"));

        var written = await client.PutAsJsonAsync("/api/v1/admin/catalog/seven-zip", app, Json);
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);

        var read = await client.GetFromJsonAsync<AdminCatalogApp>("/api/v1/admin/catalog/seven-zip", Json);
        Assert.Equal("7-Zip", read!.Name);
        Assert.Equal("Igor Pavlov", read.Publisher);
        Assert.Equal("7zip_builtin", read.Action1!.PackageId);
        Assert.False(read.Hidden);

        var hidden = await client.PostAsJsonAsync("/api/v1/admin/catalog/seven-zip/hidden", new AdminCatalogHidden(true), Json);
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        Assert.True(_catalog.Find("seven-zip")!.Hidden);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/admin/catalog/seven-zip")).StatusCode);
        Assert.Null(_catalog.Find("seven-zip"));
    }

    [Fact]
    public async Task A_catalog_write_keeps_every_field_the_edit_form_holds()
    {
        var client = await Admin();
        await client.PutAsJsonAsync("/api/v1/admin/catalog/base",
            new AdminCatalogApp("base", "Base", Action1: new AdminAction1Package("base_builtin")), Json);

        var full = new AdminCatalogApp(
            "steam", "Steam", "Valve", "Games", "Games", "https://example.invalid/icon.png",
            Featured: true, Hidden: true, EngineOverride: "agent", Requirements: "A Steam account.",
            Requires: ["base"], UserRemovable: true,
            Match: new AdminMatchRule("Steam", null),
            Action1: new AdminAction1Package("steam_builtin", "1.2.3"),
            Agent: new WingetPackageDefinition("Valve.Steam", "machine"));

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/admin/catalog/steam", full, Json)).StatusCode);

        var read = await client.GetFromJsonAsync<AdminCatalogApp>("/api/v1/admin/catalog/steam", Json);
        Assert.True(read!.Featured);
        Assert.True(read.Hidden);
        Assert.True(read.UserRemovable);
        Assert.Equal("agent", read.EngineOverride);
        Assert.Equal("A Steam account.", read.Requirements);
        Assert.Equal(["base"], read.Requires);
        Assert.Equal("Steam", read.Match!.NameContains);
        Assert.Equal("1.2.3", read.Action1!.Version);
        Assert.Equal("Valve.Steam", Assert.IsType<WingetPackageDefinition>(read.Agent).Id);
    }

    [Fact]
    public async Task A_catalog_write_is_refused_when_it_is_incomplete_or_makes_a_loop()
    {
        var client = await Admin();

        var noPackage = await client.PutAsJsonAsync("/api/v1/admin/catalog/empty",
            new AdminCatalogApp("empty", "Empty"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, noPackage.StatusCode);

        // 'new' is the create form's own route, so an app with that id could never be opened again.
        var reserved = await client.PutAsJsonAsync("/api/v1/admin/catalog/new",
            new AdminCatalogApp("new", "New", Action1: new AdminAction1Package("x_builtin")), Json);
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);

        await client.PutAsJsonAsync("/api/v1/admin/catalog/a",
            new AdminCatalogApp("a", "A", Action1: new AdminAction1Package("a_builtin")), Json);
        await client.PutAsJsonAsync("/api/v1/admin/catalog/b",
            new AdminCatalogApp("b", "B", Action1: new AdminAction1Package("b_builtin"), Requires: ["a"]), Json);

        var loop = await client.PutAsJsonAsync("/api/v1/admin/catalog/a",
            new AdminCatalogApp("a", "A", Action1: new AdminAction1Package("a_builtin"), Requires: ["b"]), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, loop.StatusCode);
    }

    [Fact]
    public async Task An_app_an_install_refers_to_cannot_be_deleted()
    {
        var client = await Admin();
        await client.PutAsJsonAsync("/api/v1/admin/catalog/chrome",
            new AdminCatalogApp("chrome", "Chrome", Action1: new AdminAction1Package("chrome_builtin")), Json);
        _devices.Add("PC-A", "endpoint-a");
        SeedInstall("PC-A", "chrome", InstallState.Succeeded, DateTimeOffset.Now.AddHours(-1));

        var refused = await client.DeleteAsync("/api/v1/admin/catalog/chrome");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Hide it instead", await refused.Content.ReadAsStringAsync());
        Assert.NotNull(_catalog.Find("chrome"));
    }

    [Fact]
    public async Task The_catalog_exports_what_an_import_puts_back()
    {
        var client = await Admin();
        var file = """
            { "apps": [ { "id": "vlc", "name": "VLC", "action1": { "packageId": "vlc_builtin", "version": "latest" } } ] }
            """;

        var imported = await client.PostAsync("/api/v1/admin/catalog/import",
            new StringContent(file, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.Equal(1, (await imported.Content.ReadFromJsonAsync<AdminCatalogImported>(Json))!.Imported);

        var exported = await client.GetStringAsync("/api/v1/admin/catalog/export");
        Assert.Contains("\"vlc\"", exported);

        // The export is the import format, which is the bargain the command line makes as well.
        _catalog.Delete("vlc");
        var again = await client.PostAsync("/api/v1/admin/catalog/import",
            new StringContent(exported, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.NotNull(_catalog.Find("vlc"));
    }

    [Fact]
    public async Task A_catalog_import_that_is_not_a_catalog_is_refused()
    {
        var client = await Admin();

        var broken = await client.PostAsync("/api/v1/admin/catalog/import",
            new StringContent("{ not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, broken.StatusCode);

        var empty = await client.PostAsync("/api/v1/admin/catalog/import",
            new StringContent("", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task The_package_helpers_answer_the_way_the_edit_form_does()
    {
        var client = await Admin();

        var search = await client.PostAsJsonAsync("/api/v1/admin/catalog/action1/search", new AdminPackageSearch("chrome"), Json);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var found = await search.Content.ReadFromJsonAsync<List<AdminPackageResult>>(Json);
        Assert.NotEmpty(found!);

        var blank = await client.PostAsJsonAsync("/api/v1/admin/catalog/action1/verify", new AdminPackageRef(""), Json);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);

        var resolved = await client.PostAsJsonAsync("/api/v1/admin/catalog/action1/verify",
            new AdminPackageRef(found![0].Id, "latest"), Json);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);
        Assert.True((await resolved.Content.ReadFromJsonAsync<AdminPackageVerified>(Json))!.Ok);

        // A package the repository does not publish comes back as a plain no, not as an error.
        var unknown = await client.PostAsJsonAsync("/api/v1/admin/catalog/action1/verify",
            new AdminPackageRef("missing_package", "latest"), Json);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.False((await unknown.Content.ReadFromJsonAsync<AdminPackageVerified>(Json))!.Ok);

        // Both agent helpers refuse a malformed argument before they reach the network.
        var badUrl = await client.PostAsJsonAsync("/api/v1/admin/catalog/package/hash", new AdminInstallerRequest("not a url"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);

        var badWinget = await client.PostAsJsonAsync("/api/v1/admin/catalog/package/winget", new AdminPackageRef("not-an-id"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, badWinget.StatusCode);
    }

    // ---- devices ------------------------------------------------------------------------------

    [Fact]
    public async Task A_device_is_added_read_changed_rotated_and_removed()
    {
        var client = await Admin();

        var added = await client.PostAsJsonAsync("/api/v1/admin/devices", new AdminDeviceCreate("PC-NEW", "endpoint-new"), Json);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var issued = await added.Content.ReadFromJsonAsync<AdminDeviceToken>(Json);
        Assert.StartsWith("apd_", issued!.DeviceToken);

        var detail = await client.GetFromJsonAsync<AdminDeviceDetail>($"/api/v1/admin/devices/{issued.DeviceId}", Json);
        Assert.Equal("PC-NEW", detail!.Device.Name);
        Assert.Equal("endpoint-new", detail.Device.EndpointId);
        Assert.True(detail.Device.Enabled);

        var updated = await client.PutAsJsonAsync($"/api/v1/admin/devices/{issued.DeviceId}",
            new AdminDeviceUpdate("PC-RENAMED", "endpoint-new", Enabled: false, EnginePreference: "agent"), Json);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var after = await updated.Content.ReadFromJsonAsync<AdminDevice>(Json);
        Assert.Equal("PC-RENAMED", after!.Name);
        Assert.False(after.Enabled);
        Assert.Equal("agent", after.EnginePreference);

        var rotated = await client.PostAsync($"/api/v1/admin/devices/{issued.DeviceId}/rotate-token", null);
        var second = await rotated.Content.ReadFromJsonAsync<AdminDeviceToken>(Json);
        Assert.NotEqual(issued.DeviceToken, second!.DeviceToken);
        Assert.Null(_devices.Authenticate(issued.DeviceToken));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/v1/admin/devices/{issued.DeviceId}")).StatusCode);
        Assert.Null(_devices.Find(issued.DeviceId));
    }

    [Fact]
    public async Task Adding_a_device_that_already_exists_is_refused_rather_than_rotating_its_token()
    {
        var existing = _devices.Add("PC-A", "endpoint-a");

        var response = await (await Admin()).PostAsJsonAsync("/api/v1/admin/devices", new AdminDeviceCreate("PC-A"), Json);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(_devices.Authenticate(existing));
    }

    [Fact]
    public async Task A_device_with_an_install_in_flight_cannot_be_removed_or_renamed_onto_another()
    {
        _devices.Add("PC-A", "endpoint-a");
        _devices.Add("PC-B", "endpoint-b");
        var a = _devices.FindByName("PC-A")!;
        SeedInstall("PC-A", "chrome", InstallState.Running, DateTimeOffset.Now, deviceId: a.Id);
        var client = await Admin();

        var removed = await client.DeleteAsync($"/api/v1/admin/devices/{a.Id}");
        Assert.Equal(HttpStatusCode.Conflict, removed.StatusCode);

        var clash = await client.PutAsJsonAsync($"/api/v1/admin/devices/{a.Id}", new AdminDeviceUpdate("PC-B", "endpoint-a"), Json);
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("PC-A", _devices.Find(a.Id)!.Name);
    }

    [Fact]
    public async Task The_device_list_names_the_key_each_one_enrolled_with()
    {
        var key = _keys.Create("rollout", EnrollmentEngine.Agent, null, null, "admin");
        _devices.Enroll("machine-1", "PC-ENROLLED", null, grantAgent: true, "0.6.0", key.Key.Id);

        var page = await (await Admin()).GetFromJsonAsync<AdminPage<AdminDevice>>("/api/v1/admin/devices?search=ENROLLED", Json);

        var device = Assert.Single(page!.Items);
        Assert.Equal("rollout", device.EnrolledWithKeyName);
        Assert.True(device.HasAgent);
        Assert.Equal("0.6.0", device.AgentVersion);
    }

    // ---- keys ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_key_is_created_once_with_its_plaintext_then_listed_without_it()
    {
        var client = await Admin();

        var created = await client.PostAsJsonAsync("/api/v1/admin/keys",
            new EnrollmentKeyCreate("rollout", "agent", DateTimeOffset.UtcNow.AddDays(7), 5), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var key = await created.Content.ReadFromJsonAsync<EnrollmentKeyCreated>(Json);
        Assert.StartsWith(EnrollmentKeyStore.Prefix, key!.Plaintext);
        Assert.Equal("agent", key.Key.DefaultEngine);
        Assert.Equal(5, key.Key.MaxUses);
        Assert.Equal(TestDatabase.AdminUsername, key.Key.CreatedBy);

        // The list carries the prefix and never the secret, because the secret was never stored.
        var listed = await client.GetStringAsync("/api/v1/admin/keys");
        Assert.DoesNotContain(key.Plaintext, listed);
        Assert.Contains(key.Key.KeyPrefix, listed);
    }

    [Fact]
    public async Task A_key_is_revoked_and_its_attempts_are_readable()
    {
        var key = _keys.Create("rollout", EnrollmentEngine.Both, null, null, "admin");
        new EnrollmentEventStore(_test.Database).Record(key.Key.Id, null, "10.0.0.5", EnrollmentOutcome.KeyRefused);
        var client = await Admin();

        var one = await client.GetFromJsonAsync<EnrollmentKeySummary>($"/api/v1/admin/keys/{key.Key.Id}", Json);
        Assert.Equal("rollout", one!.Name);
        Assert.Equal("active", one.Status);

        var revoked = await client.PostAsync($"/api/v1/admin/keys/{key.Key.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal("revoked", (await revoked.Content.ReadFromJsonAsync<EnrollmentKeySummary>(Json))!.Status);

        var events = await client.GetFromJsonAsync<List<EnrollmentKeyEvent>>($"/api/v1/admin/keys/{key.Key.Id}/events", Json);
        Assert.Equal("key-refused", Assert.Single(events!).Outcome);

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/v1/admin/keys/nothing/revoke", null)).StatusCode);
    }

    [Fact]
    public async Task A_key_with_no_name_or_a_dead_expiry_is_refused()
    {
        var client = await Admin();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/admin/keys", new EnrollmentKeyCreate(""), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/admin/keys",
                new EnrollmentKeyCreate("late", "agent", DateTimeOffset.UtcNow.AddDays(-1)), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/admin/keys", new EnrollmentKeyCreate("odd", "nonsense"), Json)).StatusCode);
    }

    // ---- administrators -----------------------------------------------------------------------

    [Fact]
    public async Task An_administrator_is_added_listed_and_disabled()
    {
        var client = await Admin();

        var added = await client.PostAsJsonAsync("/api/v1/admin/admins",
            new AdminAccountCreate("second", TestDatabase.AdminPassword), Json);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var account = await added.Content.ReadFromJsonAsync<AdminAccount>(Json);
        Assert.Equal("second", account!.Username);
        Assert.False(account.Disabled);

        var page = await client.GetFromJsonAsync<AdminPage<AdminAccount>>("/api/v1/admin/admins", Json);
        Assert.Equal(2, page!.Total);

        var disabled = await client.PostAsync($"/api/v1/admin/admins/{account.Id}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.True((await disabled.Content.ReadFromJsonAsync<AdminAccount>(Json))!.Disabled);
    }

    [Fact]
    public async Task An_administrator_cannot_disable_the_account_they_are_signed_in_with()
    {
        var me = _admins.Find(TestDatabase.AdminUsername)!;
        _admins.Add("second", TestDatabase.AdminPassword);

        var response = await (await Admin()).PostAsync($"/api/v1/admin/admins/{me.Id}/disable", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(_admins.FindById(me.Id)!.Disabled);
    }

    /// <summary>
    /// The point of disabling somebody is that they stop being able to act, and a token that lived
    /// until its expiry would leave them able to for up to thirty days after the decision was made.
    /// </summary>
    [Fact]
    public async Task Disabling_an_administrator_cuts_the_token_they_are_holding()
    {
        _admins.Add("second", TestDatabase.AdminPassword);
        var second = _admins.Find("second")!;
        var theirs = await Admin("second");
        Assert.Equal(HttpStatusCode.OK, (await theirs.GetAsync("/api/v1/admin/dashboard")).StatusCode);

        var disabled = await (await Admin()).PostAsync($"/api/v1/admin/admins/{second.Id}/disable", null);

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await theirs.GetAsync("/api/v1/admin/dashboard")).StatusCode);
        Assert.Empty(new AdminSessionStore(_test.Database).ListFor(second.Id));
    }

    [Fact]
    public async Task A_reset_password_signs_that_account_out_everywhere()
    {
        _admins.Add("second", TestDatabase.AdminPassword);
        var second = _admins.Find("second")!;
        var theirs = await Admin("second");
        var mine = await Admin();

        var reset = await mine.PostAsJsonAsync($"/api/v1/admin/admins/{second.Id}/reset-password",
            new AdminPasswordReset("a-brand-new-long-password"), Json);

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await theirs.GetAsync("/api/v1/admin/dashboard")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mine.GetAsync("/api/v1/admin/dashboard")).StatusCode);
        Assert.NotNull(_admins.Verify("second", "a-brand-new-long-password"));
    }

    [Fact]
    public async Task A_short_password_and_a_duplicate_name_are_refused()
    {
        var client = await Admin();

        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/admin/admins", new AdminAccountCreate("short", "tiny"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/v1/admin/admins",
                new AdminAccountCreate(TestDatabase.AdminUsername, TestDatabase.AdminPassword), Json)).StatusCode);

        var me = _admins.Find(TestDatabase.AdminUsername)!;
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/v1/admin/admins/{me.Id}/reset-password", new AdminPasswordReset("tiny"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/api/v1/admin/admins/nothing/disable", null)).StatusCode);
    }

    // ---- settings -----------------------------------------------------------------------------

    [Fact]
    public async Task The_default_engine_is_read_written_and_refused_when_it_is_neither()
    {
        var client = await Admin();

        Assert.Equal(EngineLabel.Action1,
            (await client.GetFromJsonAsync<AdminSettings>("/api/v1/admin/settings", Json))!.DefaultEngine);

        var saved = await client.PutAsJsonAsync("/api/v1/admin/settings", new AdminSettings("Agent"), Json);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(EngineLabel.Agent, (await saved.Content.ReadFromJsonAsync<AdminSettings>(Json))!.DefaultEngine);

        var refused = await client.PutAsJsonAsync("/api/v1/admin/settings", new AdminSettings("intune"), Json);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(EngineLabel.Agent,
            (await client.GetFromJsonAsync<AdminSettings>("/api/v1/admin/settings", Json))!.DefaultEngine);
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>A client holding a live <c>apa_</c> token for the named administrator.</summary>
    private async Task<HttpClient> Admin(string username = TestDatabase.AdminUsername, string? deviceName = null)
    {
        var client = _factory.CreateClient();
        var issued = await client.PostAsJsonAsync("/api/v1/admin/session",
            new { username, password = TestDatabase.AdminPassword, deviceName }, Json);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, string method, string path)
        => client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            // An empty JSON object, so a route that binds a body is refused by the policy rather than
            // by model binding. Authorisation runs first either way; this keeps the failure unambiguous.
            Content = method is "POST" or "PUT" ? new StringContent("{}", Encoding.UTF8, "application/json") : null,
        });

    private InstallRecord SeedInstall(
        string device,
        string appId,
        InstallState state,
        DateTimeOffset requestedAt,
        string? requestedBy = null,
        string? deviceId = null)
    {
        var record = new InstallRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            DeviceId = deviceId,
            DeviceName = device,
            AppId = appId,
            AppName = appId.ToUpperInvariant(),
            State = state,
            RequestedAt = requestedAt,
            CompletedAt = state is InstallState.Queued or InstallState.Running ? null : requestedAt.AddMinutes(2),
            RequestedBy = requestedBy,
        };
        Assert.True(_installs.Upsert(record));
        return record;
    }

    private string SeedRequest(string text)
    {
        if (_devices.FindByName("PC-REQ") is null)
        {
            _devices.Add("PC-REQ", "endpoint-req");
        }

        return _requests.CreateForDeviceId(_devices.FindByName("PC-REQ")!.Id, @"CORP\ann", text).Id;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}

/// <summary>Every line the server logged during a test, so an assertion can say what is not in one.</summary>
internal sealed class RecordedLogs
{
    private readonly ConcurrentQueue<string> _messages = new();

    public IReadOnlyList<string> Messages => [.. _messages];

    public void Add(string message) => _messages.Enqueue(message);
}

internal sealed class RecordingLoggerProvider(RecordedLogs logs) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(logs);

    public void Dispose()
    {
    }

    private sealed class RecordingLogger(RecordedLogs logs) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => logs.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
