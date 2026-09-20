using System.Net;
using System.Text;

using AppPortal.Agent.Update;

namespace AppPortal.Agent.Tests;

public sealed class ReleaseFeedTests
{
    private const string Repository = "someone/fork";

    [Fact]
    public async Task The_newest_release_yields_its_version_its_msi_and_its_checksums()
    {
        var handler = new Canned(HttpStatusCode.OK, Payload("v0.4.1", "AppPortal-0.4.1-x64.msi", "SHA256SUMS"));

        var release = await Feed(handler).GetLatestAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Equal(Version.Parse("0.4.1.0"), release.Version);
        Assert.Equal("v0.4.1", release.Tag);
        Assert.Equal("AppPortal-0.4.1-x64.msi", release.MsiName);
        Assert.Equal("https://releases.invalid/AppPortal-0.4.1-x64.msi", release.MsiUrl.ToString());
        Assert.Equal("https://releases.invalid/SHA256SUMS", release.ChecksumsUrl?.ToString());
        // GitHub answers 403 to a request without one, and a fleet that stops updating on a header is
        // a fleet nobody notices has stopped.
        Assert.Contains("AppPortal.Agent", handler.Last?.Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task A_repository_with_no_release_yet_is_not_an_error()
    {
        var release = await Feed(new Canned(HttpStatusCode.NotFound, "{}")).GetLatestAsync(CancellationToken.None);

        Assert.Null(release);
    }

    [Fact]
    public async Task A_release_without_the_msi_for_its_own_version_is_refused()
    {
        // The client zip alone is what a 0.3.x release published, and running one of those through
        // Windows Installer is not a thing that can happen.
        var handler = new Canned(HttpStatusCode.OK, Payload("v0.4.1", "AppPortal-client-win-x64.zip", "SHA256SUMS"));

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => Feed(handler).GetLatestAsync(CancellationToken.None));

        Assert.Contains("AppPortal-0.4.1-x64.msi", ex.Message);
    }

    [Fact]
    public async Task A_tag_that_is_not_a_version_is_refused_rather_than_guessed_at()
    {
        var handler = new Canned(HttpStatusCode.OK, Payload("nightly", "AppPortal-0.4.1-x64.msi", "SHA256SUMS"));

        await Assert.ThrowsAsync<InvalidDataException>(() => Feed(handler).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_release_with_no_checksums_still_parses_and_says_so()
    {
        // The refusal belongs to whatever downloads it, which can then report a reason instead of a
        // release that simply never appears.
        var handler = new Canned(HttpStatusCode.OK, Payload("v0.4.1", "AppPortal-0.4.1-x64.msi", null));

        var release = await Feed(handler).GetLatestAsync(CancellationToken.None);

        Assert.NotNull(release);
        Assert.Null(release.ChecksumsUrl);
    }

    [Fact]
    public async Task A_device_asks_anonymously_and_only_a_build_sends_a_token()
    {
        // The token exists for a hosted runner, whose anonymous allowance is shared with every other
        // runner on the same address and always spent. A device has no credential and must never look
        // as though it does.
        var anonymous = new Canned(HttpStatusCode.OK, Payload("v0.4.1", "AppPortal-0.4.1-x64.msi", "SHA256SUMS"));
        await Feed(anonymous).GetLatestAsync(CancellationToken.None);
        Assert.Null(anonymous.Last?.Headers.Authorization);

        var build = new Canned(HttpStatusCode.OK, Payload("v0.4.1", "AppPortal-0.4.1-x64.msi", "SHA256SUMS"));
        await Feed(build, "ghs_a_build_token").GetLatestAsync(CancellationToken.None);
        Assert.Equal("Bearer", build.Last?.Headers.Authorization?.Scheme);
        Assert.Equal("ghs_a_build_token", build.Last?.Headers.Authorization?.Parameter);
    }

    private static GitHubReleaseFeed Feed(Canned handler, string? token = null)
        => new(new HttpClient(handler), Repository, token);

    private static string Payload(string tag, params string?[] assets)
    {
        var items = assets
            .Where(a => a is not null)
            .Select(a => $$"""{"name":"{{a}}","browser_download_url":"https://releases.invalid/{{a}}"}""");
        return $$"""{"tag_name":"{{tag}}","assets":[{{string.Join(",", items)}}]}""";
    }

    private sealed class Canned(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Last { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
