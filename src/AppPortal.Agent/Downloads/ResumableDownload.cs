using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

using AppPortal.Shared;

namespace AppPortal.Agent.Downloads;

/// <summary>Why a download stopped, in words the person who asked for the install can act on.</summary>
public sealed class DownloadFailedException(string message) : Exception(message);

/// <summary>
/// Fetches an installer and proves it is the one the catalog described. A game is several gigabytes
/// over an office connection, so the transfer has to survive a dropped connection and a service
/// restart: the partial file stays on disk and the next attempt asks for the rest of it by range.
/// </summary>
public sealed class ResumableDownload(HttpClient http, InstallerCache cache, ILogger logger)
{
    /// <summary>How often progress is worth reporting. Every chunk would be thousands of calls a minute.</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(5);

    /// <summary>The verified file, downloading it first unless the cache already holds it.</summary>
    public async Task<string> FetchAsync(DirectPackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(cache.Directory);
        var final = cache.PathFor(definition.Sha256, definition.InstallerType);
        if (File.Exists(final))
        {
            // Named after its own hash, so its presence is the proof. Touched so that the eviction
            // sweep counts it as recently wanted rather than stale.
            logger.LogInformation("Using the cached installer for {Hash}", definition.Sha256[..8]);
            File.SetLastAccessTimeUtc(final, DateTime.UtcNow);
            return final;
        }

        if (!cache.HasRoomFor(definition.SizeBytes, out var reason))
        {
            throw new DownloadFailedException(reason);
        }

        var partial = final + ".part";
        await TransferAsync(definition, partial, progress, ct);

        progress.Report((100, "Verifying download"));
        var actual = await HashAsync(partial, ct);
        if (!actual.Equals(definition.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // The file is not what the catalog described. Keeping it would mean resuming onto a body
            // that is already wrong, and running it is out of the question.
            Try(() => File.Delete(partial));
            throw new DownloadFailedException("Checksum mismatch. The file at that URL is not the one the catalog describes.");
        }

        File.Move(partial, final, overwrite: true);
        cache.Evict();
        return final;
    }

    private async Task TransferAsync(DirectPackageDefinition definition, string partial,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
        if (have > definition.SizeBytes)
        {
            // Longer than the catalog says it should be, so it is not a prefix of the right file.
            Try(() => File.Delete(partial));
            have = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, DirectPackageDefinition.ValidateUrl(definition.Url));
        if (have > 0)
        {
            request.Headers.Range = new RangeHeaderValue(have, null);
            logger.LogInformation("Resuming the download at {Bytes} bytes", have);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // The server has a different file now, or the partial is already the whole of it.
            Try(() => File.Delete(partial));
            throw new DownloadFailedException("The server would not resume the download. It will start again on the next attempt.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new DownloadFailedException($"The installer could not be downloaded ({(int)response.StatusCode} {response.ReasonPhrase}).");
        }

        // A server that ignores Range answers 200 with the whole body, and appending that to what is
        // already on disk would build a file that is the beginning twice.
        var appending = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (have > 0 && !appending)
        {
            logger.LogInformation("The server ignored the range request; starting again");
            have = 0;
        }

        var total = response.Content.Headers.ContentLength is { } length && length > 0
            ? have + length
            : definition.SizeBytes;

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = new FileStream(partial, appending ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, 1024 * 128, useAsync: true);

        var buffer = new byte[1024 * 128];
        var written = have;
        var lastPercent = -1;
        var lastReport = DateTimeOffset.MinValue;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) != 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            written += read;
            var percent = total > 0 ? (int)Math.Clamp(written * 100 / total, 0, 100) : 0;
            var now = DateTimeOffset.UtcNow;
            if (percent != lastPercent || now - lastReport > ReportEvery)
            {
                lastPercent = percent;
                lastReport = now;
                progress.Report((percent, $"Downloading {percent}%"));
            }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
    }

    private static void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
