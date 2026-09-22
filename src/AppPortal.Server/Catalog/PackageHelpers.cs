using System.Net;
using System.Security.Cryptography;

using AppPortal.Shared;

namespace AppPortal.Server.Catalog;

public sealed record InstallerHash(string Sha256, long SizeBytes);

public sealed record WingetLookup(bool? Exists, string Message);

public sealed class PackageHelpers(HttpClient client)
{
    public const long DefaultMaxDownloadBytes = 2L * 1024 * 1024 * 1024;

    public static PackageHelpers Shared { get; } = new(new HttpClient(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    public async Task<InstallerHash> FetchAndHashAsync(string url, long maxBytes, CancellationToken ct)
    {
        var uri = DirectPackageDefinition.ValidateUrl(url);
        if (maxBytes <= 0)
        {
            throw new InvalidDataException("The download size limit must be positive.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException($"The download exceeds the {maxBytes} byte limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long size = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, deadline.Token)) != 0)
        {
            // Content-Length is optional and untrusted, so the stream itself enforces the limit.
            if (read > maxBytes - size)
            {
                throw new InvalidDataException($"The download exceeds the {maxBytes} byte limit.");
            }

            hash.AppendData(buffer, 0, read);
            size += read;
        }

        if (size == 0)
        {
            throw new InvalidDataException("The download was empty.");
        }

        return new InstallerHash(Convert.ToHexStringLower(hash.GetHashAndReset()), size);
    }

    public async Task<WingetLookup> LookupWingetAsync(string id, CancellationToken ct, string source = WingetSources.Winget)
    {
        new WingetPackageDefinition(id, "machine", Source: source).Validate();
        if (source == WingetSources.Store)
        {
            // winget-pkgs holds no manifest for a Store app, so asking it would report a correct id as
            // a missing one. The shape of the id is all we can check from a server, and Validate has
            // just checked it.
            return new WingetLookup(null, $"{id} looks like a Store product id. The Store cannot be checked from the server, so try it on one PC before offering it to everybody.");
        }

        var path = string.Join('/', id.Split('.').Select(Uri.EscapeDataString));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/{char.ToLowerInvariant(id[0])}/{path}");
        request.Headers.UserAgent.ParseAdd("AppPortal/0.5");
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new WingetLookup(true, $"Found {id} in winget-pkgs."),
                HttpStatusCode.NotFound => new WingetLookup(false, $"No manifest found for {id}. Check the id and its capitalization."),
                _ => new WingetLookup(null, "Winget lookup is unavailable. You can still save the definition."),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return new WingetLookup(null, "Winget lookup is unavailable. You can still save the definition.");
        }
    }
}
