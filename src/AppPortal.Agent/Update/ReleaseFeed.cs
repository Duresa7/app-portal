using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using AppPortal.Shared;

namespace AppPortal.Agent.Update;

/// <summary>What one release offers: the version it carries, and where its MSI and checksums are.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string MsiName, Uri MsiUrl, Uri? ChecksumsUrl);

/// <summary>
/// What the newest release is. An interface because everything downstream of it is decided by what it
/// answers, and a test that has to reach GitHub to ask is a test that fails when GitHub is slow.
/// </summary>
public interface IReleaseFeed
{
    /// <summary>Null when the repository has no release yet.</summary>
    Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct);
}

/// <summary>
/// The newest release of one GitHub repository. No token is involved: sixty anonymous calls an hour
/// is plenty for a machine that checks a few times a day.
/// </summary>
public sealed class GitHubReleaseFeed(HttpClient http, string repository) : IReleaseFeed
{
    public const string ChecksumsAssetName = "SHA256SUMS";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The MSI a release publishes carries its version in the name, as CI writes it.</summary>
    public static string MsiAssetName(Version version) => $"AppPortal-{VersionText.Short(version)}-x64.msi";

    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        // GitHub answers 403 to a request without one, so it is set here rather than left to whichever
        // HttpClient this feed was handed.
        request.Headers.UserAgent.ParseAdd($"AppPortal.Agent/{ThisVersion()}");
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<ReleaseDto>(Json, ct)
                      ?? throw new InvalidDataException("The release feed returned an empty body.");
        if (!VersionText.TryParse(release.TagName, out var version))
        {
            throw new InvalidDataException($"The release tag '{release.TagName}' is not a version.");
        }

        var msiName = MsiAssetName(version);
        var msi = Asset(release, msiName) ?? throw new InvalidDataException($"Release {release.TagName} has no {msiName}.");
        var sums = Asset(release, ChecksumsAssetName);
        return new ReleaseInfo(version, release.TagName!, msiName, new Uri(msi), sums is null ? null : new Uri(sums));
    }

    private static string? Asset(ReleaseDto release, string name)
        => release.Assets?.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl;

    private static string ThisVersion()
        => VersionText.TryParse(typeof(GitHubReleaseFeed).Assembly.GetName().Version?.ToString(), out var v) ? VersionText.Short(v) : "0.0.0";

    private sealed class ReleaseDto
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        public List<AssetDto>? Assets { get; set; }
    }

    private sealed class AssetDto
    {
        public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}

/// <summary>
/// Which repository the releases come from. An <c>"updateRepository": "owner/name"</c> in client.json
/// points a test fleet at a fork; anything that is not a plain owner and name is ignored rather than
/// pasted into a URL, because that setting decides where this PC takes software from.
/// </summary>
public static partial class UpdateRepository
{
    public const string Default = "Duresa7/app-portal";

    public static string Resolve(string? configured)
        => configured is not null && Pattern().IsMatch(configured) ? configured : Default;

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex Pattern();
}
