using System.Net;
using System.Text;

using AppPortal.Client.Services;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

public sealed class AdminApiClientTests
{
    private const string Server = "https://portal.example";

    private const string EmptyPage = """{"items":[],"offset":0,"limit":50,"hasMore":false,"total":0}""";

    /// <summary>
    /// Every method against the route the server maps for it, from <c>Admin/Api/*.cs</c> and the session
    /// endpoints in <c>AdminAuth.cs</c>. A method pointed at the wrong route compiles and fails only
    /// against a live server, so the table is the check.
    /// </summary>
    public static IEnumerable<object[]> Routes()
    {
        static object[] Row(Func<IAdminApiClient, Task> call, string method, string pathAndQuery, string body = "{}")
            => [call, method, pathAndQuery, body];

        var ct = CancellationToken.None;
        yield return Row(api => api.SignOutAsync(ct), "DELETE", "/api/v1/admin/session", "");
        yield return Row(api => api.GetSessionsAsync(ct), "GET", "/api/v1/admin/sessions", "[]");
        yield return Row(api => api.RevokeSessionAsync("s1", ct), "DELETE", "/api/v1/admin/sessions/s1", "");
        yield return Row(api => api.GetDashboardAsync(ct), "GET", "/api/v1/admin/dashboard");

        yield return Row(api => api.GetInstallsAsync(new AdminInstallFilter(), 0, 50, ct), "GET", "/api/v1/admin/installs?limit=50&offset=0", EmptyPage);
        yield return Row(
            api => api.GetInstallsAsync(new AdminInstallFilter("PC 1", "7-zip", InstallState.Failed, @"CONTOSO\alee",
                new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 22), AwaitingRestart: true), 50, 25, ct),
            "GET",
            "/api/v1/admin/installs?Device=PC%201&App=7-zip&State=Failed&Requester=CONTOSO%5Calee&From=2026-09-01&To=2026-09-22&Restart=1&limit=25&offset=50",
            EmptyPage);
        yield return Row(api => api.GetInstallAsync("i1", ct), "GET", "/api/v1/admin/installs/i1");
        yield return Row(api => api.CancelInstallAsync("i1", ct), "POST", "/api/v1/admin/installs/i1/cancel");

        yield return Row(api => api.GetRequestsAsync(AppRequestStatus.Pending, 0, 50, ct), "GET", "/api/v1/admin/requests?status=pending&limit=50&offset=0", EmptyPage);
        yield return Row(api => api.GetRequestsAsync(null, 0, 50, ct), "GET", "/api/v1/admin/requests?status=all&limit=50&offset=0", EmptyPage);
        yield return Row(api => api.ApproveRequestAsync("r1", "ok", ct), "POST", "/api/v1/admin/requests/r1/approve");
        yield return Row(api => api.DenyRequestAsync("r1", null, ct), "POST", "/api/v1/admin/requests/r1/deny");

        yield return Row(api => api.GetCatalogAsync("chrome", 0, 50, ct), "GET", "/api/v1/admin/catalog?Search=chrome&limit=50&offset=0", EmptyPage);
        yield return Row(api => api.GetCatalogAppAsync("vscode", ct), "GET", "/api/v1/admin/catalog/vscode");
        yield return Row(api => api.SaveCatalogAppAsync(new AdminCatalogApp("vscode", "Visual Studio Code"), ct), "PUT", "/api/v1/admin/catalog/vscode");
        yield return Row(api => api.DeleteCatalogAppAsync("vscode", ct), "DELETE", "/api/v1/admin/catalog/vscode", "");
        yield return Row(api => api.SetCatalogAppHiddenAsync("vscode", true, ct), "POST", "/api/v1/admin/catalog/vscode/hidden");
        yield return Row(api => api.ImportCatalogAsync("""{"apps":[]}""", ct), "POST", "/api/v1/admin/catalog/import", """{"imported":0}""");
        yield return Row(api => api.ExportCatalogAsync(ct), "GET", "/api/v1/admin/catalog/export", """{"apps":[]}""");
        yield return Row(api => api.SearchAction1PackagesAsync("zoom", ct), "POST", "/api/v1/admin/catalog/action1/search", "[]");
        yield return Row(api => api.VerifyAction1PackageAsync(new AdminPackageRef("Zoom"), ct), "POST", "/api/v1/admin/catalog/action1/verify");
        yield return Row(api => api.HashInstallerAsync("https://example.com/setup.msi", ct), "POST", "/api/v1/admin/catalog/package/hash");
        yield return Row(api => api.LookupWingetAsync(new AdminPackageRef("Git.Git"), ct), "POST", "/api/v1/admin/catalog/package/winget");

        yield return Row(api => api.GetDevicesAsync(null, 0, 50, ct), "GET", "/api/v1/admin/devices?limit=50&offset=0", EmptyPage);
        yield return Row(api => api.GetDeviceAsync("d1", ct), "GET", "/api/v1/admin/devices/d1");
        yield return Row(api => api.CreateDeviceAsync(new AdminDeviceCreate("PC-9"), ct), "POST", "/api/v1/admin/devices");
        yield return Row(api => api.UpdateDeviceAsync("d1", new AdminDeviceUpdate("PC-1"), ct), "PUT", "/api/v1/admin/devices/d1");
        yield return Row(api => api.RotateDeviceTokenAsync("d1", ct), "POST", "/api/v1/admin/devices/d1/rotate-token");
        yield return Row(api => api.DeleteDeviceAsync("d1", ct), "DELETE", "/api/v1/admin/devices/d1", "");

        yield return Row(api => api.GetKeysAsync(0, 50, ct), "GET", "/api/v1/admin/keys?limit=50&offset=0", EmptyPage);
        yield return Row(api => api.GetKeyAsync("k1", ct), "GET", "/api/v1/admin/keys/k1");
        yield return Row(api => api.CreateKeyAsync(new EnrollmentKeyCreate("Office"), ct), "POST", "/api/v1/admin/keys");
        yield return Row(api => api.RevokeKeyAsync("k1", ct), "POST", "/api/v1/admin/keys/k1/revoke");
        yield return Row(api => api.GetKeyEventsAsync("k1", 5, ct), "GET", "/api/v1/admin/keys/k1/events?limit=5", "[]");

        yield return Row(api => api.GetAdminsAsync(0, 50, ct), "GET", "/api/v1/admin/admins?limit=50&offset=0", EmptyPage);
        yield return Row(api => api.CreateAdminAsync(new AdminAccountCreate("helpdesk", "long enough password"), ct), "POST", "/api/v1/admin/admins");
        yield return Row(api => api.DisableAdminAsync("a1", ct), "POST", "/api/v1/admin/admins/a1/disable");
        yield return Row(api => api.ResetAdminPasswordAsync("a1", "long enough password", ct), "POST", "/api/v1/admin/admins/a1/reset-password", "");

        yield return Row(api => api.GetSettingsAsync(ct), "GET", "/api/v1/admin/settings");
        yield return Row(api => api.UpdateSettingsAsync(new AdminSettings("agent"), ct), "PUT", "/api/v1/admin/settings");
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task Each_method_calls_the_route_the_server_maps(Func<IAdminApiClient, Task> call, string method, string pathAndQuery, string body)
    {
        var handler = new FakeHandler(_ => Reply(body.Length == 0 ? HttpStatusCode.NoContent : HttpStatusCode.OK, body));
        var api = new AdminApiClient(Server, handler) { Token = "apa_test" };

        await call(api);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(method, request.Method);
        Assert.Equal(pathAndQuery, request.Uri.PathAndQuery);
        Assert.Equal("Bearer apa_test", request.Authorization);
    }

    [Fact]
    public async Task Sign_in_sends_the_three_fields_and_no_old_token()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, """{"token":"apa_new","expiresAt":"2026-10-22T00:00:00Z"}"""));
        var api = new AdminApiClient(Server, handler) { Token = "apa_old" };

        var signedIn = await api.SignInAsync("admin", "correct horse", "RECEPTION-01", CancellationToken.None);

        Assert.Equal("apa_new", signedIn.Token);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/v1/admin/session", request.Uri.PathAndQuery);
        Assert.Null(request.Authorization);
        Assert.Equal("""{"username":"admin","password":"correct horse","deviceName":"RECEPTION-01"}""", request.Body);
    }

    [Fact]
    public async Task A_server_below_a_path_keeps_its_path()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, "{}"));
        var api = new AdminApiClient("https://portal.example/app-portal/", handler) { Token = "apa_test" };

        await api.GetDashboardAsync(CancellationToken.None);

        Assert.Equal("https://portal.example/app-portal/api/v1/admin/dashboard", Assert.Single(handler.Requests).Uri.ToString());
    }

    [Fact]
    public async Task An_id_cannot_become_a_different_route()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, "{}"));
        var api = new AdminApiClient(Server, handler) { Token = "apa_test" };

        await api.GetCatalogAppAsync("a/b?c", CancellationToken.None);

        Assert.Equal("/api/v1/admin/catalog/a%2Fb%3Fc", Assert.Single(handler.Requests).Uri.PathAndQuery);
    }

    [Fact]
    public async Task Import_sends_the_catalog_file_as_it_is()
    {
        const string file = """{ "apps": [ { "id": "x", "name": "X" } ] }""";
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, """{"imported":1}"""));
        var api = new AdminApiClient(Server, handler) { Token = "apa_test" };

        var result = await api.ImportCatalogAsync(file, CancellationToken.None);

        Assert.Equal(1, result.Imported);
        Assert.Equal(file, Assert.Single(handler.Requests).Body);
    }

    [Fact]
    public async Task The_server_message_is_what_the_caller_sees()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.Conflict, """{"message":"That request had already been decided."}"""));
        var api = new AdminApiClient(Server, handler) { Token = "apa_test" };

        var ex = await Assert.ThrowsAsync<PortalApiException>(() => api.ApproveRequestAsync("r1", null, CancellationToken.None));

        Assert.Equal("That request had already been decided.", ex.Message);
        Assert.Equal(HttpStatusCode.Conflict, ex.Status);
    }

    [Fact]
    public async Task A_body_that_is_not_an_error_message_falls_back_to_the_status()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>Bad gateway</html>", Encoding.UTF8, "text/html"),
        });
        var api = new AdminApiClient(Server, handler) { Token = "apa_test" };

        var ex = await Assert.ThrowsAsync<PortalApiException>(() => api.GetDashboardAsync(CancellationToken.None));

        Assert.Equal("The server answered HTTP 502.", ex.Message);
    }

    [Fact]
    public async Task A_refused_token_says_the_session_ended_and_tells_its_holder()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var api = new AdminApiClient(Server, handler) { Token = "apa_revoked" };
        var raised = 0;
        api.Unauthorized += (_, _) => raised++;

        var ex = await Assert.ThrowsAsync<PortalApiException>(() => api.GetDashboardAsync(CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.Status);
        Assert.Equal(AdminApiClient.SessionEndedMessage, ex.Message);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task A_refused_sign_in_is_a_wrong_password_not_an_ended_session()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.Unauthorized,
            """{"message":"That user name and password do not match an enabled administrator."}"""));
        var api = new AdminApiClient(Server, handler);
        var raised = 0;
        api.Unauthorized += (_, _) => raised++;

        var ex = await Assert.ThrowsAsync<PortalApiException>(() => api.SignInAsync("admin", "wrong", null, CancellationToken.None));

        Assert.Equal("That user name and password do not match an enabled administrator.", ex.Message);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task A_throttled_sign_in_says_how_long_to_wait()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var api = new AdminApiClient(Server, handler);

        var ex = await Assert.ThrowsAsync<PortalApiException>(() => api.SignInAsync("admin", "wrong", null, CancellationToken.None));

        Assert.Contains("fifteen minutes", ex.Message);
    }

    [Fact]
    public async Task An_unreachable_server_is_a_readable_error()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("No connection could be made."));
        var api = new AdminApiClient(Server, handler) { Token = "apa_test" };

        var ex = await Assert.ThrowsAsync<PortalApiException>(() => api.GetDashboardAsync(CancellationToken.None));

        Assert.StartsWith("Cannot reach the App Portal server.", ex.Message);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Seen(string Method, Uri Uri, string? Authorization, string? Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Seen(request.Method.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            return respond(request);
        }
    }
}
