using System.Net;
using System.Text;

using AppPortal.Server.Action1;
using AppPortal.Server.Options;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AppPortal.Server.Tests;

/// <summary>
/// Action1 answers a lookup for a package identifier it does not know with HTTP 200 and no body.
/// The first live run of `catalog verify` died on exactly that, and the same read sits behind POST /installs.
/// </summary>
public sealed class Action1ClientTests
{
    private static Action1Client NewClient(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var options = new Action1Options
        {
            BaseUrl = "https://action1.example/api/3.0",
            OrgId = "org-0000",
            ClientId = "client",
            ClientSecret = "secret",
        };
        var http = new HttpClient(new StubHandler(respond));
        return new Action1Client(http, Microsoft.Extensions.Options.Options.Create(options), NullLogger<Action1Client>.Instance);
    }

    private static HttpResponseMessage Token()
        => new(HttpStatusCode.OK) { Content = new StringContent("""{ "access_token": "t", "expires_in": 3600 }""", Encoding.UTF8, "application/json") };

    [Fact]
    public async Task An_empty_200_for_an_unknown_package_means_no_version_rather_than_a_crash()
    {
        var client = NewClient(request => request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token")
            ? Token()
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("", Encoding.UTF8, "application/json") });

        var version = await client.ResolvePackageVersionAsync("Nobody_Nothing_0_builtin", "latest", CancellationToken.None);

        Assert.Null(version);
    }

    [Fact]
    public async Task A_body_that_is_not_json_becomes_an_Action1Exception_without_the_organisation_in_it()
    {
        var client = NewClient(request => request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token")
            ? Token()
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>", Encoding.UTF8, "text/html") });

        var ex = await Assert.ThrowsAsync<Action1Exception>(() => client.ResolvePackageVersionAsync("Some_Package_builtin", "latest", CancellationToken.None));

        Assert.Contains("could not be read", ex.Message);
        Assert.DoesNotContain("org-0000", ex.Message);
        Assert.Contains("{org}", ex.Message);
    }

    [Fact]
    public async Task A_real_package_still_resolves_to_its_newest_published_version()
    {
        var client = NewClient(request => request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token")
            ? Token()
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    { "id": "P", "name": "P", "versions": [
                        { "id": "v1", "version": "1.2.0", "status": "Published" },
                        { "id": "v2", "version": "1.10.0", "status": "Published" },
                        { "id": "v3", "version": "2.0.0", "status": "Draft" } ] }
                    """, Encoding.UTF8, "application/json"),
            });

        var version = await client.ResolvePackageVersionAsync("P", "latest", CancellationToken.None);

        Assert.NotNull(version);
        Assert.Equal("1.10.0", version.Version);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
