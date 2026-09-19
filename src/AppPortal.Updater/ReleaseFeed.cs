using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Shared;

namespace AppPortal.Updater;

public sealed record ReleaseInfo(Version Version, string Tag, Uri ZipUrl, Uri? ChecksumsUrl);

/// <summary>
/// The newest release of one GitHub repository. No token is involved: sixty anonymous calls an hour
/// is plenty for a machine that checks a few times a day.
/// </summary>
public sealed class ReleaseFeed
{
    public const string ZipAssetName = "AppPortal-client-win-x64.zip";
    public const string ChecksumsAssetName = "SHA256SUMS";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _repository;

    public ReleaseFeed(HttpClient http, string repository)
    {
        _http = http;
        _repository = repository;
    }

    /// <summary>Null when the repository has no release yet.</summary>
    public async Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await _http.SendAsync(request, ct);
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

        var zip = Asset(release, ZipAssetName) ?? throw new InvalidDataException($"Release {release.TagName} has no {ZipAssetName}.");
        var sums = Asset(release, ChecksumsAssetName);
        return new ReleaseInfo(version, release.TagName!, new Uri(zip), sums is null ? null : new Uri(sums));
    }

    private static string? Asset(ReleaseDto release, string name)
        => release.Assets?.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))?.BrowserDownloadUrl;

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
