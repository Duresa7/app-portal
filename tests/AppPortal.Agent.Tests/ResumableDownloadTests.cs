using System.Net;
using System.Security.Cryptography;

using AppPortal.Agent.Downloads;
using AppPortal.Shared;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

/// <summary>
/// Against a real Kestrel, because the behaviour under test is HTTP behaviour: a server that honours a
/// range request, one that ignores it, and one that stops mid-body. A fake handler would only prove
/// that the fake was written to agree with the code.
/// </summary>
public sealed class ResumableDownloadTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _body = RandomNumberGenerator.GetBytes(512 * 1024);
    private WebApplication _server = null!;
    private string _url = "";

    /// <summary>Set by a test to make the next response stop after this many bytes.</summary>
    private int? _dropAfter;

    private bool _ignoreRange;

    private readonly List<string> _requests = [];

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _server = builder.Build();
        _server.MapGet("/installer.exe", async (HttpContext context) =>
        {
            var range = context.Request.Headers.Range.ToString();
            _requests.Add(range.Length == 0 ? "whole" : range);
            var from = 0;
            if (!_ignoreRange && range.StartsWith("bytes=", StringComparison.Ordinal)
                && int.TryParse(range["bytes=".Length..].TrimEnd('-'), out var parsed))
            {
                from = parsed;
                context.Response.StatusCode = StatusCodes.Status206PartialContent;
                context.Response.Headers.ContentRange = $"bytes {from}-{_body.Length - 1}/{_body.Length}";
            }

            var slice = _body.AsMemory(from);
            if (_dropAfter is { } drop && drop < slice.Length)
            {
                _dropAfter = null;
                context.Response.ContentLength = slice.Length;
                await context.Response.Body.WriteAsync(slice[..drop]);
                await context.Response.Body.FlushAsync();
                context.Abort();
                return;
            }

            context.Response.ContentLength = slice.Length;
            await context.Response.Body.WriteAsync(slice);
        });

        await _server.StartAsync();
        _url = _server.Urls.First() + "/installer.exe";
    }

    private DirectPackageDefinition Definition => new(_url, Convert.ToHexStringLower(SHA256.HashData(_body)),
        "exe", "/S", _body.Length);

    [Fact]
    public async Task A_download_that_finishes_is_verified_and_kept_under_its_own_hash()
    {
        var path = await Download().FetchAsync(Definition, new Progress(), CancellationToken.None);

        Assert.Equal(_body, await File.ReadAllBytesAsync(path));
        Assert.EndsWith(Definition.Sha256 + ".exe", path);
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task A_download_cut_off_mid_body_resumes_from_the_byte_it_stopped_at()
    {
        _dropAfter = 100 * 1024;
        var download = Download();

        await Assert.ThrowsAnyAsync<Exception>(() => download.FetchAsync(Definition, new Progress(), CancellationToken.None));

        // However much of the body survived the abort is what is on disk, and that is the number the
        // next request has to ask from. Asserting a fixed offset would only be asserting how much of a
        // dropped response the socket happened to deliver.
        var partial = Directory.GetFiles(Path.Combine(_root, "downloads"), "*.part").Single();
        var have = new FileInfo(partial).Length;
        Assert.InRange(have, 1, _body.Length - 1);

        var path = await download.FetchAsync(Definition, new Progress(), CancellationToken.None);

        Assert.Equal(_body, await File.ReadAllBytesAsync(path));
        // The server's own log is the proof: the second request asked for the rest, not the whole file.
        Assert.Equal("whole", _requests[0]);
        Assert.Equal($"bytes={have}-", _requests[1]);
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_still_yields_the_right_file()
    {
        _dropAfter = 100 * 1024;
        var download = Download();
        await Assert.ThrowsAnyAsync<Exception>(() => download.FetchAsync(Definition, new Progress(), CancellationToken.None));

        // Appending a whole body to a partial one would build the beginning twice, and the hash would
        // be the only thing that noticed.
        _ignoreRange = true;
        var path = await download.FetchAsync(Definition, new Progress(), CancellationToken.None);

        Assert.Equal(_body, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_file_that_is_not_what_the_catalog_described_is_refused_and_deleted()
    {
        var wrong = Definition with { Sha256 = new string('a', 64) };

        var failure = await Assert.ThrowsAsync<DownloadFailedException>(
            () => Download().FetchAsync(wrong, new Progress(), CancellationToken.None));

        Assert.Contains("Checksum mismatch", failure.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "downloads")));
    }

    [Fact]
    public async Task The_second_job_for_the_same_installer_does_not_fetch_it_again()
    {
        var download = Download();
        await download.FetchAsync(Definition, new Progress(), CancellationToken.None);
        var requests = _requests.Count;

        await download.FetchAsync(Definition, new Progress(), CancellationToken.None);

        Assert.Equal(requests, _requests.Count);
    }

    [Fact]
    public async Task A_device_without_room_is_told_before_the_download_rather_than_after()
    {
        var cache = new InstallerCache(Path.Combine(_root, "downloads"), NullLogger.Instance);
        var download = new ResumableDownload(new HttpClient(), cache, NullLogger.Instance);
        var enormous = Definition with { SizeBytes = long.MaxValue / 2 };

        var failure = await Assert.ThrowsAsync<DownloadFailedException>(
            () => download.FetchAsync(enormous, new Progress(), CancellationToken.None));

        Assert.Contains("free to install this", failure.Message);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task Progress_climbs_and_ends_at_the_verification()
    {
        var reports = new List<(int percent, string detail)>();

        await Download().FetchAsync(Definition, new Progress(reports.Add), CancellationToken.None);

        Assert.Contains(reports, r => r.detail.StartsWith("Downloading"));
        Assert.Equal("Verifying download", reports[^1].detail);
        Assert.True(reports.Select(r => r.percent).SequenceEqual(reports.Select(r => r.percent).Order()));
    }

    private ResumableDownload Download()
    {
        var cache = new InstallerCache(Path.Combine(_root, "downloads"), NullLogger.Instance);
        return new ResumableDownload(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }, cache, NullLogger.Instance);
    }

    public async Task DisposeAsync()
    {
        await _server.StopAsync();
        await _server.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Progress(Action<(int percent, string detail)>? report = null) : IProgress<(int percent, string detail)>
    {
        public void Report((int percent, string detail) value) => report?.Invoke(value);
    }
}
