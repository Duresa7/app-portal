using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Agent.Jobs;
using AppPortal.Agent.Update;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

public sealed class SelfUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-update-tests", Guid.NewGuid().ToString("N"));
    private readonly UpdatePaths _paths;

    public SelfUpdateTests()
    {
        _paths = new UpdatePaths(Path.Combine(_root, "install"), Path.Combine(_root, "state"));
        Directory.CreateDirectory(_paths.InstallDir);
        Directory.CreateDirectory(_paths.StateDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0.0")]
    [InlineData("0.2.0.0", "0.2.0.0")]
    [InlineData("0.2.0+abc123", "0.2.0.0")]
    [InlineData("1.4", "1.4.0.0")]
    [InlineData("v2.0.1-rc1", "2.0.1.0")]
    public void Version_text_normalises_to_four_parts(string text, string expected)
    {
        Assert.True(VersionText.TryParse(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Fact]
    public void Version_text_rejects_garbage_and_compares_correctly()
    {
        Assert.False(VersionText.TryParse("latest", out _));
        Assert.False(VersionText.TryParse("", out _));
        Assert.True(VersionText.IsNewer("v0.2.0", "0.1.1.0"));
        Assert.False(VersionText.IsNewer("0.2.0", "0.2.0.0"));
        Assert.False(VersionText.IsNewer(null, "0.2.0"));
    }

    [Fact]
    public void Checksum_file_yields_the_msi_hash_in_either_mode()
    {
        var hash = new string('a', 64);
        var name = GitHubReleaseFeed.MsiAssetName(Version.Parse("0.4.1.0"));
        var sums = $"# comment\n{hash}  {name}\n{new string('b', 64)} *other.msi\n";
        Assert.Equal(hash, Checksums.Parse(sums, name));
        Assert.Equal(new string('b', 64), Checksums.Parse(sums, "other.msi"));
        Assert.Null(Checksums.Parse(sums, "missing.msi"));
        Assert.Null(Checksums.Parse("short  " + name, name));
    }

    [Fact]
    public void Repository_override_is_honoured_only_when_it_is_an_owner_and_a_name()
    {
        var path = Path.Combine(_paths.StateDir, "client.json");
        File.WriteAllText(path, """{ "serverUrl": "http://x", "updateRepository": "someone/fork" }""");
        Assert.Equal("someone/fork", UpdateRepository.Resolve(PortalSettings.Load(path).UpdateRepository));

        File.WriteAllText(path, """{ "updateRepository": "https://evil.example/x" }""");
        Assert.Equal(UpdateRepository.Default, UpdateRepository.Resolve(PortalSettings.Load(path).UpdateRepository));

        File.WriteAllText(path, "not json");
        Assert.Equal(UpdateRepository.Default, UpdateRepository.Resolve(PortalSettings.Load(path).UpdateRepository));
        Assert.Equal(UpdateRepository.Default, UpdateRepository.Resolve(null));
    }

    [Fact]
    public void Windows_installer_is_asked_for_a_silent_install_that_restarts_nothing()
    {
        var arguments = SelfUpdate.MsiexecArguments(@"C:\data\updates\AppPortal-0.4.1-x64.msi", @"C:\data\updates\update-0.4.1.log");

        Assert.Contains(@"/i ""C:\data\updates\AppPortal-0.4.1-x64.msi""", arguments);
        Assert.Contains("/qn", arguments);
        // Without /norestart an msi may restart the PC on its own, in the middle of somebody's day.
        Assert.Contains("/norestart", arguments);
        Assert.Contains(@"/l*v ""C:\data\updates\update-0.4.1.log""", arguments);
    }

    [Fact]
    public async Task The_newest_release_being_the_installed_one_needs_nothing()
    {
        var processes = new FakeProcesses();
        var status = await Update(new FakeFeed(Release("0.4.0")), new FakeDownloader(_paths), processes).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateResult.UpToDate, status.Result);
        Assert.Equal("0.4.0", status.InstalledVersion);
        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task A_newer_release_is_installed_when_nobody_has_the_client_open()
    {
        var processes = new FakeProcesses();
        Stale("0.3.9");

        var status = await Update(new FakeFeed(Release("0.4.1")), new FakeDownloader(_paths), processes).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateResult.Installed, status.Result);
        Assert.Equal("0.4.1", status.InstalledVersion);
        var started = Assert.Single(processes.Started);
        Assert.Equal("msiexec.exe", started.File);
        Assert.Contains("AppPortal-0.4.1-x64.msi", started.Arguments);
        Assert.Contains("update-0.4.1.log", started.Arguments);
        Assert.Empty(Directory.GetFiles(_paths.UpdatesDir, "*.msi"));
    }

    [Fact]
    public async Task An_open_client_is_waited_for_rather_than_upgraded_underneath()
    {
        var processes = new FakeProcesses();
        Stale("0.3.9");

        var status = await Update(new FakeFeed(Release("0.4.1")), new FakeDownloader(_paths), processes, clientIsRunning: true)
            .RunAsync(CancellationToken.None);

        Assert.Equal(UpdateResult.Available, status.Result);
        // What the client's "Restart to update" banner reads: downloaded, verified, waiting for them.
        Assert.Equal("0.4.1", status.StagedVersion);
        Assert.Empty(processes.Started);
        var kept = Assert.Single(Directory.GetFiles(_paths.UpdatesDir, "*.msi"));
        Assert.Equal("AppPortal-0.4.1-x64.msi", Path.GetFileName(kept));
    }

    [Fact]
    public async Task Windows_installer_failing_says_so_and_points_at_its_log()
    {
        var status = await Update(new FakeFeed(Release("0.4.1")), new FakeDownloader(_paths), new FakeProcesses(1603))
            .RunAsync(CancellationToken.None);

        Assert.Equal(UpdateResult.Failed, status.Result);
        Assert.Contains("1603", status.Message);
        Assert.Contains("update-0.4.1.log", status.Message);
        // The installed build is untouched, so the client should still be told what it is running.
        Assert.Equal("0.4.0", status.InstalledVersion);
        Assert.True(File.Exists(_paths.MsiPath("AppPortal-0.4.1-x64.msi")), "a failure worth reading is worth not re-downloading");
    }

    [Fact]
    public async Task An_exit_code_that_only_asks_for_a_restart_is_a_success()
    {
        var status = await Update(new FakeFeed(Release("0.4.1")), new FakeDownloader(_paths), new FakeProcesses(3010))
            .RunAsync(CancellationToken.None);

        Assert.Equal(UpdateResult.Installed, status.Result);
        Assert.Contains("restart", status.Message);
    }

    [Fact]
    public async Task A_feed_that_cannot_be_reached_changes_nothing()
    {
        var processes = new FakeProcesses();
        var status = await Update(new FakeFeed(null, new HttpRequestException("no route")), new FakeDownloader(_paths), processes)
            .RunAsync(CancellationToken.None);

        Assert.Equal(UpdateResult.Offline, status.Result);
        Assert.Equal("0.4.0", status.InstalledVersion);
        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task A_download_that_does_not_verify_is_a_failure_and_nothing_is_run()
    {
        var processes = new FakeProcesses();
        var downloads = new FakeDownloader(_paths, new InvalidDataException("AppPortal-0.4.1-x64.msi does not match its published SHA-256 and was discarded."));

        var status = await Update(new FakeFeed(Release("0.4.1")), downloads, processes).RunAsync(CancellationToken.None);

        Assert.Equal(UpdateResult.Failed, status.Result);
        Assert.Contains("SHA-256", status.Message);
        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task The_status_file_keeps_the_shape_the_client_reads()
    {
        await Update(new FakeFeed(Release("0.4.1")), new FakeDownloader(_paths), new FakeProcesses(), clientIsRunning: true)
            .RunAsync(CancellationToken.None);

        // The client deserialises with these options and nothing else. A rename here is a silent
        // banner that never appears again.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var status = JsonSerializer.Deserialize<UpdateStatus>(File.ReadAllText(_paths.StatusPath), options);

        Assert.NotNull(status);
        Assert.Equal(UpdateResult.Available, status.Result);
        Assert.Equal("0.4.0", status.InstalledVersion);
        Assert.Equal("0.4.1", status.LatestVersion);
        Assert.Equal("0.4.1", status.StagedVersion);
        Assert.True(status.CheckedAt > DateTimeOffset.MinValue);
    }

    private static ReleaseInfo Release(string version) => new(
        Version.Parse(version + ".0"),
        "v" + version,
        $"AppPortal-{version}-x64.msi",
        new Uri($"https://releases.invalid/AppPortal-{version}-x64.msi"),
        new Uri("https://releases.invalid/SHA256SUMS"));

    /// <summary>An MSI left behind by an earlier release, which a finished pass should not keep.</summary>
    private void Stale(string version)
    {
        Directory.CreateDirectory(_paths.UpdatesDir);
        File.WriteAllText(_paths.MsiPath($"AppPortal-{version}-x64.msi"), "old");
    }

    private SelfUpdate Update(IReleaseFeed feed, IUpdateDownloader downloads, IProcessRunner processes, bool clientIsRunning = false)
        => new(feed, downloads, processes, new FakeClient(clientIsRunning), _paths, NullLogger<SelfUpdate>.Instance,
            () => Version.Parse("0.4.0.0"));

    private sealed class FakeFeed(ReleaseInfo? release, Exception? throws = null) : IReleaseFeed
    {
        public Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
            => throws is null ? Task.FromResult(release) : Task.FromException<ReleaseInfo?>(throws);
    }

    private sealed class FakeDownloader(UpdatePaths paths, Exception? throws = null) : IUpdateDownloader
    {
        public Task<string> FetchAsync(ReleaseInfo release, CancellationToken ct)
        {
            if (throws is not null)
            {
                return Task.FromException<string>(throws);
            }

            Directory.CreateDirectory(paths.UpdatesDir);
            var path = paths.MsiPath(release.MsiName);
            File.WriteAllText(path, "msi " + release.Tag);
            return Task.FromResult(path);
        }
    }

    private sealed class FakeClient(bool running) : IClientPresence
    {
        public bool IsRunning() => running;
    }

    private sealed class FakeProcesses(int exitCode = 0) : IProcessRunner
    {
        public List<(string File, string Arguments)> Started { get; } = [];

        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            Started.Add((file, arguments));
            return Task.FromResult(new ProcessResult(exitCode, ""));
        }
    }
}
