using AppPortal.Agent.Downloads;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

public sealed class InstallerCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));

    public InstallerCacheTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_file_is_named_after_its_own_hash_so_its_presence_proves_its_content()
    {
        var cache = new InstallerCache(_root, NullLogger.Instance);
        var hash = new string('A', 64);

        var path = cache.PathFor(hash, "msi");

        Assert.Equal(Path.Combine(_root, new string('a', 64) + ".msi"), path);
    }

    [Fact]
    public void What_nobody_has_wanted_for_a_week_goes()
    {
        var cache = new InstallerCache(_root, NullLogger.Instance, keepFor: TimeSpan.FromDays(7));
        var stale = Write("stale.exe", 10);
        var fresh = Write("fresh.exe", 10);
        File.SetLastAccessTimeUtc(stale, DateTime.UtcNow.AddDays(-8));

        cache.Evict();

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }

    [Fact]
    public void Over_the_limit_the_least_recently_wanted_go_first()
    {
        var cache = new InstallerCache(_root, NullLogger.Instance, maxBytes: 250);
        var oldest = Write("a.exe", 100);
        var middle = Write("b.exe", 100);
        var newest = Write("c.exe", 100);
        File.SetLastAccessTimeUtc(oldest, DateTime.UtcNow.AddHours(-3));
        File.SetLastAccessTimeUtc(middle, DateTime.UtcNow.AddHours(-2));
        File.SetLastAccessTimeUtc(newest, DateTime.UtcNow.AddHours(-1));

        cache.Evict();

        Assert.False(File.Exists(oldest));
        Assert.True(File.Exists(middle));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void A_download_still_running_is_not_evicted_from_under_itself()
    {
        var cache = new InstallerCache(_root, NullLogger.Instance, maxBytes: 1);
        var partial = Write("half.exe.part", 500);

        cache.Evict();

        Assert.True(File.Exists(partial));
    }

    [Fact]
    public void A_cache_that_does_not_exist_yet_is_not_an_error()
    {
        new InstallerCache(Path.Combine(_root, "absent"), NullLogger.Instance).Evict();
    }

    [Fact]
    public void A_download_larger_than_the_disk_is_refused_with_both_numbers()
    {
        var cache = new InstallerCache(_root, NullLogger.Instance);

        Assert.False(cache.HasRoomFor(long.MaxValue / 2, out var reason));
        Assert.Contains("free to install this", reason);
        Assert.Contains("GB", reason);
    }

    [Fact]
    public void A_download_that_fits_is_allowed()
    {
        Assert.True(new InstallerCache(_root, NullLogger.Instance).HasRoomFor(1024, out var reason));
        Assert.Equal("", reason);
    }

    private string Write(string name, int bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
