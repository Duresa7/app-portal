using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

using Avalonia.Media.Imaging;

namespace AppPortal.Client.Services;

/// <summary>Downloads catalog icons once per session. Failures are silent: the card falls back to a lettered tile.</summary>
public sealed class IconCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _icons = new(StringComparer.OrdinalIgnoreCase);

    public Task<Bitmap?> GetAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return Task.FromResult<Bitmap?>(null);
        }

        return _icons.GetOrAdd(url, static u => DownloadAsync(u));
    }

    private static async Task<Bitmap?> DownloadAsync(string url)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length == 0 || bytes.Length > 2_000_000)
            {
                return null;
            }

            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
