using System.Net;
using System.Text.RegularExpressions;

using AppPortal.Server.Admin;
using AppPortal.Server.Enrollment;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class AdminKeysPageTests : IDisposable
{
    private const string Password = "a-long-enough-password";

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;

    public AdminKeysPageTests()
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

        new AdminStore(_test.Database).Add("admin", Password);
    }

    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    private static async Task<string> TokenFrom(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"The form on {path} carried no antiforgery token.");
        return match.Groups[1].Value;
    }

    private async Task<HttpClient> SignedIn()
    {
        var client = Browser();
        var token = await TokenFrom(client, "/admin/login");
        var response = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "admin",
            ["Password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    [Fact]
    public async Task The_page_is_closed_to_anyone_not_signed_in()
    {
        var response = await Browser().GetAsync("/admin/keys");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creating_a_key_shows_the_plaintext_once_and_then_never_again()
    {
        var client = await SignedIn();
        var token = await TokenFrom(client, "/admin/keys");

        var response = await client.PostAsync("/admin/keys?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["NewName"] = "Sales laptops",
            ["NewEngine"] = "both",
            ["NewMaxUses"] = "5",
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        var shown = Regex.Match(html, "id=\"new-key\"[^>]*value=\"(ape_[A-Z2-7]{32})\"");
        Assert.True(shown.Success, "The created key was not shown on the page that created it.");
        Assert.Contains("Sales laptops", html, StringComparison.Ordinal);

        // Every later render of the page must have forgotten it.
        var again = await client.GetStringAsync("/admin/keys");
        Assert.DoesNotContain(shown.Groups[1].Value, again, StringComparison.Ordinal);
        Assert.Contains("Sales laptops", again, StringComparison.Ordinal);
        Assert.Contains("0 of 5", again, StringComparison.Ordinal);

        // And the key that was shown is the one the store will actually spend.
        Assert.NotNull(new EnrollmentKeyStore(_test.Database).TryConsume(shown.Groups[1].Value));
    }

    [Fact]
    public async Task A_bad_expiry_is_reported_and_no_key_is_created()
    {
        var client = await SignedIn();
        var token = await TokenFrom(client, "/admin/keys");

        var response = await client.PostAsync("/admin/keys?handler=Create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["NewName"] = "Bad date",
            ["NewEngine"] = "action1",
            ["NewExpires"] = "the day after tomorrow",
            ["__RequestVerificationToken"] = token,
        }));

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("is not a date", html, StringComparison.Ordinal);
        Assert.Empty(new EnrollmentKeyStore(_test.Database).List());
    }

    [Fact]
    public async Task Revoking_from_the_page_stops_the_key_being_spent()
    {
        var keys = new EnrollmentKeyStore(_test.Database);
        var created = keys.Create("To revoke", EnrollmentEngine.Action1, null, null, "admin");

        var client = await SignedIn();
        var token = await TokenFrom(client, "/admin/keys");

        var response = await client.PostAsync("/admin/keys?handler=Revoke", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = created.Key.Id,
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(EnrollmentKeyStatus.Revoked, keys.Find(created.Key.Id)!.Status);
        Assert.Null(keys.TryConsume(created.Plaintext));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
