using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

using AppPortal.Server.Admin;
using AppPortal.Server.Options;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Server.Tests;

/// <summary>
/// Directory sign-in without a directory: the LDAP work sits behind <see cref="IDirectoryAuthenticator"/>,
/// so everything the portal decides — provisioning, precedence, refusals — is tested here, and only the
/// bind itself needs a domain controller.
/// </summary>
public sealed class DirectorySignInTests : IDisposable
{
    private const string LocalPassword = "a-long-enough-password";
    private const string DirectoryPassword = "the-directory-knows-this-one";

    private readonly TestDatabase _test = new();
    private readonly FakeDirectory _directory = new();
    private readonly AdminStore _admins;
    private readonly AdminSignIn _signIn;

    public DirectorySignInTests()
    {
        _admins = new AdminStore(_test.Database);
        _signIn = new AdminSignIn(_admins, _directory, NullLogger<AdminSignIn>.Instance);
    }

    public void Dispose() => _test.Dispose();

    [Theory]
    [InlineData("ALPHASEC\\dkadi", "ALPHASEC\\dkadi", "ALPHASEC", "dkadi")]
    [InlineData("alphasec\\dkadi", "alphasec\\dkadi", "ALPHASEC", "dkadi")]
    [InlineData("dkadi@example.com", "dkadi@example.com", "ALPHASEC", "dkadi")]
    [InlineData("dkadi", "ALPHASEC\\dkadi", "ALPHASEC", "dkadi")]
    public void A_typed_name_becomes_a_bind_name(string typed, string bind, string domain, string account)
    {
        var options = new DirectoryOptions { NetBiosDomain = "ALPHASEC", UpnSuffix = "example.com" };

        var (bindName, resolvedDomain, resolvedAccount) = DirectoryName.Resolve(typed, options);

        Assert.Equal(bind, bindName);
        Assert.Equal(domain, resolvedDomain);
        Assert.Equal(account, resolvedAccount);
    }

    [Fact]
    public void Without_a_netbios_domain_a_bare_name_binds_as_a_upn()
    {
        var options = new DirectoryOptions { UpnSuffix = "example.com" };

        var (bindName, _, _) = DirectoryName.Resolve("dkadi", options);

        Assert.Equal("dkadi@example.com", bindName);
    }

    [Fact]
    public void A_filter_cannot_be_rewritten_by_a_user_name()
        => Assert.Equal("\\2a\\29\\28admin", DirectoryName.Escape("*)(admin"));

    [Fact]
    public void A_group_member_gets_an_account_the_first_time_and_reuses_it_after()
    {
        var first = _signIn.Authenticate("dkadi", DirectoryPassword);
        var second = _signIn.Authenticate("ALPHASEC\\dkadi", DirectoryPassword);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal("ALPHASEC\\dkadi", first.Username);
        Assert.True(first.IsDirectory);
        Assert.Single(_admins.All());
    }

    [Fact]
    public void A_wrong_directory_password_is_refused()
        => Assert.Null(_signIn.Authenticate("dkadi", "not-the-password"));

    [Fact]
    public void A_valid_account_outside_the_group_is_refused()
    {
        _directory.InGroup = false;

        Assert.Null(_signIn.Authenticate("dkadi", DirectoryPassword));
        Assert.Empty(_admins.All());
    }

    [Fact]
    public void A_local_account_still_works_when_no_controller_answers()
    {
        _admins.Add("local-admin", LocalPassword);
        _directory.Reachable = false;

        Assert.NotNull(_signIn.Authenticate("local-admin", LocalPassword));
        Assert.Null(_signIn.Authenticate("dkadi", DirectoryPassword));
    }

    [Fact]
    public void A_directory_account_disabled_in_the_portal_cannot_sign_in()
    {
        var admin = _signIn.Authenticate("dkadi", DirectoryPassword);
        Assert.NotNull(admin);
        _admins.Add("local-admin", LocalPassword);     // so the last enabled account is not the one being disabled
        _admins.SetDisabled(admin!.Username, true);

        Assert.Null(_signIn.Authenticate("dkadi", DirectoryPassword));
    }

    [Fact]
    public void A_directory_account_cannot_take_over_a_local_name()
    {
        _admins.Add("ALPHASEC\\dkadi", LocalPassword);

        Assert.Null(_signIn.Authenticate("dkadi", DirectoryPassword));
        Assert.Single(_admins.All());
        Assert.False(_admins.Find("ALPHASEC\\dkadi")!.IsDirectory);
    }

    [Fact]
    public void A_directory_account_keeps_no_password_here()
    {
        var admin = _signIn.Authenticate("dkadi", DirectoryPassword)!;

        Assert.Null(_admins.Verify(admin.Username, DirectoryPassword));
        var rejected = Assert.Throws<AdminRejectedException>(() => _admins.SetPassword(admin.Username, LocalPassword));
        Assert.Contains("directory", rejected.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Local_accounts_are_checked_before_the_directory_is_asked()
    {
        _admins.Add("dkadi", LocalPassword);

        Assert.NotNull(_signIn.Authenticate("dkadi", LocalPassword));
        Assert.Equal(0, _directory.Attempts);
    }

    [Theory]
    [InlineData("", "GROUP", "Servers")]
    [InlineData("dc01", "", "RequiredGroup")]
    public void A_half_configured_section_stops_the_server(string server, string group, string expected)
    {
        var options = new DirectoryOptions
        {
            Enabled = true,
            Servers = server.Length == 0 ? [] : [server],
            RequiredGroup = group,
            NetBiosDomain = "ALPHASEC",
        };

        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disabled_section_is_never_validated() => new DirectoryOptions().Validate();

    /// <summary>Answers like a controller holding one account in one group.</summary>
    private sealed class FakeDirectory : IDirectoryAuthenticator
    {
        public bool Enabled => true;

        public bool Reachable { get; set; } = true;

        public bool InGroup { get; set; } = true;

        public int Attempts { get; private set; }

        public DirectoryResult Authenticate(string username, string password)
        {
            Attempts++;
            if (!Reachable)
            {
                return new DirectoryResult(DirectoryOutcome.Unavailable, Detail: "No route to host");
            }

            var (_, domain, account) = DirectoryName.Resolve(username, new DirectoryOptions { NetBiosDomain = "ALPHASEC" });
            if (!string.Equals(account, "dkadi", StringComparison.OrdinalIgnoreCase) || password != DirectoryPassword)
            {
                return new DirectoryResult(DirectoryOutcome.BadCredentials);
            }

            return InGroup
                ? new DirectoryResult(
                    DirectoryOutcome.Success,
                    new DirectoryUser(account, domain, "A Person", $"CN={account},DC=example,DC=com"))
                : new DirectoryResult(DirectoryOutcome.NotInGroup, Detail: "Not a member");
        }
    }
}

/// <summary>The same thing through the sign-in page and the admin API, so the wiring is covered too.</summary>
public sealed class DirectorySignInEndToEndTests : IDisposable
{
    private const string Password = "the-directory-knows-this-one";

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;

    public DirectorySignInEndToEndTests()
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
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDirectoryAuthenticator>();
                services.AddSingleton<IDirectoryAuthenticator, OneAccountDirectory>();
            });
        });
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }

    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task A_directory_account_signs_in_through_the_page()
    {
        var client = Browser();
        var html = await client.GetStringAsync("/admin/login");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "ALPHASEC\\dkadi",
            ["Password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin", response.Headers.Location?.OriginalString);

        var dashboard = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);

        var accounts = new AdminStore(_test.Database).All();
        Assert.Equal("ALPHASEC\\dkadi", Assert.Single(accounts).Username);
        Assert.True(accounts[0].IsDirectory);
    }

    [Fact]
    public async Task A_directory_account_takes_an_api_token()
    {
        var response = await Browser().PostAsJsonAsync(
            "/api/v1/admin/session", new { username = "ALPHASEC\\dkadi", password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.StartsWith("apa_", body!["token"].ToString());
    }

    private sealed class OneAccountDirectory : IDirectoryAuthenticator
    {
        public bool Enabled => true;

        public DirectoryResult Authenticate(string username, string password)
            => username.EndsWith("dkadi", StringComparison.OrdinalIgnoreCase) && password == Password
                ? new DirectoryResult(
                    DirectoryOutcome.Success,
                    new DirectoryUser("dkadi", "ALPHASEC", "A Person", "CN=dkadi,DC=example,DC=com"))
                : new DirectoryResult(DirectoryOutcome.BadCredentials);
    }
}
