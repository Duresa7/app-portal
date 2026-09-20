using System.Security.Cryptography;

namespace AppPortal.Agent.Update;

/// <summary>
/// Puts the release MSI on disk, or refuses. An interface because the decision to install is tested
/// against a file that is simply there, without a server to serve it.
/// </summary>
public interface IUpdateDownloader
{
    /// <summary>The path of the verified MSI. Throws when it cannot be fetched or does not verify.</summary>
    Task<string> FetchAsync(ReleaseInfo release, CancellationToken ct);
}

/// <summary>
/// Streams the MSI to disk while hashing it, then compares the hash with the SHA256SUMS the release
/// publishes. A release without checksums, or a mismatch, means nothing is kept: an installer this
/// agent runs as SYSTEM is the last file on the machine that should be taken on trust.
/// </summary>
public sealed class HttpUpdateDownloader(HttpClient http, UpdatePaths paths, ILogger<HttpUpdateDownloader> logger) : IUpdateDownloader
{
    public async Task<string> FetchAsync(ReleaseInfo release, CancellationToken ct)
    {
        if (release.ChecksumsUrl is null)
        {
            throw new InvalidDataException($"Release {release.Tag} publishes no {GitHubReleaseFeed.ChecksumsAssetName}; refusing an unverified download.");
        }

        var sums = await http.GetStringAsync(release.ChecksumsUrl, ct);
        var expected = Checksums.Parse(sums, release.MsiName)
                       ?? throw new InvalidDataException($"{GitHubReleaseFeed.ChecksumsAssetName} has no entry for {release.MsiName}.");

        Directory.CreateDirectory(paths.UpdatesDir);
        var target = paths.MsiPath(release.MsiName);
        if (File.Exists(target) && string.Equals(await HashAsync(target, ct), expected, StringComparison.OrdinalIgnoreCase))
        {
            // An earlier pass already fetched it and the client was open. Downloading it again would
            // cost a person's bandwidth to arrive at the same file.
            logger.LogInformation("{Asset} is already downloaded and verified", release.MsiName);
            return target;
        }

        var partial = target + ".part";
        string actual;
        using (var response = await http.GetAsync(release.MsiUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(partial);
            using var sha = SHA256.Create();
            await using var hashing = new CryptoStream(file, sha, CryptoStreamMode.Write);
            await source.CopyToAsync(hashing, ct);
            await hashing.FlushFinalBlockAsync(ct);
            actual = Convert.ToHexStringLower(sha.Hash!);
        }

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidDataException($"{release.MsiName} does not match its published SHA-256 and was discarded.");
        }

        File.Move(partial, target, overwrite: true);
        logger.LogInformation("Downloaded {Asset}, SHA-256 verified", release.MsiName);
        return target;
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
    }
}

/// <summary>One line of sha256sum output per file: hash, whitespace, name, with an optional binary-mode asterisk.</summary>
public static class Checksums
{
    public static string? Parse(string sumsText, string fileName)
    {
        foreach (var raw in sumsText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        return null;
    }
}
