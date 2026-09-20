using AppPortal.Shared;

namespace AppPortal.Agent.Update;

/// <summary>
/// <c>--check</c>: ask the release feed what the newest release is and print it. Nothing is downloaded
/// and nothing is installed. CI runs this against the real feed, so a change to what GitHub answers
/// fails a build rather than every PC in the field on the same afternoon.
/// </summary>
/// <remarks>
/// GITHUB_TOKEN is used when the environment has one, which on a device it never does. A hosted runner
/// shares its address with every other runner on it and the anonymous allowance is always spent, so
/// without it this check reports a rate limit instead of whether the feed still parses.
/// </remarks>
public static class UpdateCheck
{
    public static async Task<int> RunAsync(CancellationToken ct)
    {
        var repository = UpdateRepository.Resolve(PortalSettings.Load().UpdateRepository);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var feed = new GitHubReleaseFeed(http, repository, Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        try
        {
            var latest = await feed.GetLatestAsync(ct);
            if (latest is null)
            {
                Console.WriteLine($"{repository} has published no release yet.");
                return 0;
            }

            Console.WriteLine($"{repository} publishes {VersionText.Short(latest.Version)} as {latest.MsiName}.");
            Console.WriteLine(latest.ChecksumsUrl is null
                ? $"No {GitHubReleaseFeed.ChecksumsAssetName}; that release would be refused."
                : $"Checksums at {latest.ChecksumsUrl}.");
            return latest.ChecksumsUrl is null ? 1 : 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            Console.Error.WriteLine($"{repository}: {ex.Message}");
            return 1;
        }
    }
}
