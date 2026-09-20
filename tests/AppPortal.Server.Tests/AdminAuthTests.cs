using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

using AppPortal.Server.Admin;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class AdminAuthTests : IDisposable
{
    private const string Password = "a-long-enough-password";

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;

    public AdminAuthTests()
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
        });

        // The factory's database is the one the store below writes to, so the account exists before the
        // first request rather than being created through a page that needs an account to reach.
        new AdminStore(_test.Database).Add("admin", Password);
    }

    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    /// <summary>Fetches a page and lifts the antiforgery token out of the form on it.</summary>
    private static async Task<string> TokenFrom(HttpClient client, string path = "/admin/login")
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"The form on {path} carried no antiforgery token.");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> SignIn(HttpClient client, string username, string password)
    {
        var token = await TokenFrom(client);
        return await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = username,
            ["Password"] = password,
            ["__RequestVerificationToken"] = token,
        }));
    }

    [Fact]
    public async Task Signing_in_sets_a_cookie_and_opens_the_dashboard()
    {
        var client = Browser();

        var response = await SignIn(client, "admin", Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location?.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("AppPortal.Admin=", StringComparison.Ordinal));

        var dashboard = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Fact]
    public async Task The_admin_area_is_closed_without_a_session()
    {
        var response = await Browser().GetAsync("/admin");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Device_routes_are_untouched_by_the_admin_area()
    {
        // The admin cookie scheme must not start answering for the device API.
        var response = await Browser().GetAsync("/api/v1/catalog");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_then_rate_limited()
    {
        var client = Browser();

        for (var attempt = 0; attempt < SignInThrottle.MaxFailures; attempt++)
        {
            var refused = await SignIn(client, "admin", "not-the-password");
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            Assert.Contains("do not match", await refused.Content.ReadAsStringAsync());
        }

        var blocked = await SignIn(client, "admin", "not-the-password");
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);

        // And the right password is refused too while the window is open, or the limit means nothing.
        var correct = await SignIn(client, "admin", Password);
        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
    }

    [Fact]
    public async Task A_disabled_administrator_cannot_sign_in()
    {
        new AdminStore(_test.Database).Add("second", Password);
        new AdminStore(_test.Database).SetDisabled("second", true);

        var response = await SignIn(Browser(), "second", Password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("do not match", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Signing_out_revokes_the_session_the_cookie_names()
    {
        var client = Browser();
        await SignIn(client, "admin", Password);
        var token = await TokenFrom(client, "/admin");

        var out1 = await client.PostAsync("/admin/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, out1.StatusCode);

        var after = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.Contains("/admin/login", after.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_bearer_session_is_issued_used_and_revoked()
    {
        var client = _factory.CreateClient();

        var issued = await client.PostAsJsonAsync("/api/v1/admin/session", new { username = "admin", password = Password });
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);

        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString();
        Assert.StartsWith("apa_", token);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var revoked = await client.DeleteAsync("/api/v1/admin/session");
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        // The same token a second time is no longer anybody.
        var again = await client.DeleteAsync("/api/v1/admin/session");
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
    }

    [Fact]
    public async Task A_bad_password_on_the_token_endpoint_is_unauthorized()
    {
        var response = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/admin/session", new { username = "admin", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_device_token_is_not_an_admin_token()
    {
        var deviceToken = new AppPortal.Server.Devices.DeviceStore(_test.Database).Add("TESTPC", "endpoint-1");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

        var response = await client.DeleteAsync("/api/v1/admin/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("seconduser")]
    [InlineData("averylongadministratorname")]
    [InlineData("Mixed.Case_Name-1")]
    public void A_user_name_is_stored_exactly_as_it_was_given(string username)
    {
        var store = new AdminStore(_test.Database);
        store.Add(username, Password);

        Assert.Equal(username, store.Find(username)!.Username);
        Assert.Contains(store.All(), a => a.Username == username);
    }

    [Fact]
    public void A_user_name_matches_without_regard_to_case()
    {
        var store = new AdminStore(_test.Database);
        store.Add("CaseAdmin", Password);

        Assert.NotNull(store.Find("caseadmin"));
        Assert.Throws<AdminRejectedException>(() => store.Add("CASEADMIN", Password));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
