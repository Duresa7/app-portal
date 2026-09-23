using System.Collections.Concurrent;

using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Agent.Update;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

public sealed class UpdateWorkerTests : IDisposable
{
    /// <summary>
    /// An entry that is not ours, shaped like the product code Windows Installer registers a product
    /// under. Nothing pins this particular GUID: it stands for every other row in Apps and Features,
    /// and it is here to be left alone.
    /// </summary>
    private const string SomebodyElsesEntry = "{8E0F9B3C-2A41-4D77-9C58-3B6E1F0A7D42}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-update-worker-tests", Guid.NewGuid().ToString("N"));
    private readonly UpdatePaths _paths;

    public UpdateWorkerTests()
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

    [Fact]
    public void The_daily_check_falls_somewhere_in_the_noon_hour()
    {
        var now = new DateTimeOffset(2026, 3, 4, 9, 30, 0, TimeSpan.Zero);

        // Every seed, because the point of the random second is that no two PCs pick the same one and
        // any of them may be the one that lands on a boundary.
        for (var seed = 0; seed < 100; seed++)
        {
            var at = UpdateWorker.NextDailyCheck(now, new Random(seed));

            Assert.True(at > now);
            Assert.Equal(now.Date, at.Date);
            Assert.Equal(12, at.Hour);
        }
    }

    [Fact]
    public void A_check_time_that_has_already_gone_today_is_tomorrow_noon()
    {
        var now = new DateTimeOffset(2026, 3, 4, 15, 0, 0, TimeSpan.Zero);

        var at = UpdateWorker.NextDailyCheck(now, new Random(7));

        Assert.Equal(now.Date.AddDays(1), at.Date);
        Assert.Equal(12, at.Hour);
    }

    [Fact]
    public async Task The_agent_looks_for_an_update_as_soon_as_it_starts()
    {
        var feed = new CountingFeed();
        var worker = Worker(feed, new FakeProcesses());

        await worker.StartAsync(CancellationToken.None);
        await WaitFor(() => feed.Calls >= 1);
        await worker.StopAsync(CancellationToken.None);

        Assert.True(File.Exists(_paths.StatusPath), "a pass always leaves the client something to read");
    }

    [Fact]
    public async Task A_request_left_by_a_client_is_answered_and_taken_away()
    {
        var feed = new CountingFeed();
        var worker = Worker(feed, new FakeProcesses());
        await worker.StartAsync(CancellationToken.None);
        await WaitFor(() => feed.Calls >= 1);

        File.WriteAllText(_paths.RequestPath, DateTimeOffset.UtcNow.ToString("O"));
        await WaitFor(() => feed.Calls >= 2);
        await worker.StopAsync(CancellationToken.None);

        // Taken before the pass runs, so a request made during one is answered by the next rather
        // than run twice.
        Assert.False(File.Exists(_paths.RequestPath));
    }

    [Fact]
    public async Task The_leftover_updater_task_from_a_zip_install_is_deleted()
    {
        var processes = new FakeProcesses();

        await Worker(new CountingFeed(), processes).RemoveLegacyTaskAsync(CancellationToken.None);

        var started = Assert.Single(processes.Started);
        Assert.Equal("schtasks.exe", started.File);
        Assert.Contains("/Delete", started.Arguments);
        Assert.Contains("\"App Portal Updater\"", started.Arguments);
        Assert.Contains("/F", started.Arguments);
    }

    [Fact]
    public async Task A_machine_that_never_had_that_task_is_not_a_failure()
    {
        // schtasks says "cannot find the file specified" and exits non-zero on every PC that was
        // installed from an MSI in the first place, which is nearly all of them.
        var processes = new FakeProcesses(1);

        await Worker(new CountingFeed(), processes).RemoveLegacyTaskAsync(CancellationToken.None);

        Assert.Single(processes.Started);
    }

    [Fact]
    public async Task The_uninstall_entry_from_a_zip_install_is_deleted()
    {
        var registry = new FakeRegistry(UpdateWorker.LegacyUninstallKey, SomebodyElsesEntry);

        await Worker(new CountingFeed(), new FakeProcesses(), registry).RemoveLegacyInstallAsync(CancellationToken.None);

        Assert.DoesNotContain(UpdateWorker.LegacyUninstallKey, registry.Entries);
    }

    [Fact]
    public async Task The_entry_Windows_Installer_owns_is_left_where_it_is()
    {
        // Both rows read "App Portal" in Apps & Features. Taking the wrong one away would leave Windows
        // Installer with a product it thinks is there, which is the state this whole cleanup exists to avoid.
        var registry = new FakeRegistry(UpdateWorker.LegacyUninstallKey, SomebodyElsesEntry);

        await Worker(new CountingFeed(), new FakeProcesses(), registry).RemoveLegacyInstallAsync(CancellationToken.None);

        Assert.Equal([SomebodyElsesEntry], registry.Entries);
    }

    [Fact]
    public async Task The_retired_updater_and_its_script_go_from_the_install_folder()
    {
        WriteInstallFile("AppPortal.Updater.exe");
        WriteInstallFile("Uninstall-AppPortalClient.ps1");
        WriteInstallFile("AppPortal.Agent.exe");

        await Worker(new CountingFeed(), new FakeProcesses()).RemoveLegacyInstallAsync(CancellationToken.None);

        Assert.False(File.Exists(Path.Combine(_paths.InstallDir, "AppPortal.Updater.exe")));
        Assert.False(File.Exists(Path.Combine(_paths.InstallDir, "Uninstall-AppPortalClient.ps1")));
        Assert.True(File.Exists(Path.Combine(_paths.InstallDir, "AppPortal.Agent.exe")), "the MSI owns the rest of that folder");
    }

    [Fact]
    public async Task A_machine_that_was_never_a_zip_install_has_nothing_to_report()
    {
        // Nothing to find anywhere: schtasks exits non-zero because there is no such task, there is no
        // uninstall entry and no leftover file. That is nearly every PC in the fleet, every time it starts.
        var logs = new RecordedLogs();

        await Worker(new CountingFeed(), new FakeProcesses(1), new FakeRegistry(SomebodyElsesEntry), logs)
            .RemoveLegacyInstallAsync(CancellationToken.None);

        Assert.DoesNotContain(logs.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task A_file_that_will_not_go_is_a_warning_and_the_cleanup_carries_on()
    {
        // A directory standing where the updater should be. File.Delete refuses it on Windows and on
        // Linux alike, which is as close as a test gets to a stale process holding the real one open.
        Directory.CreateDirectory(Path.Combine(_paths.InstallDir, "AppPortal.Updater.exe"));
        WriteInstallFile("Uninstall-AppPortalClient.ps1");
        var logs = new RecordedLogs();

        await Worker(new CountingFeed(), new FakeProcesses(), new FakeRegistry(), logs)
            .RemoveLegacyInstallAsync(CancellationToken.None);

        Assert.Contains(logs.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.False(File.Exists(Path.Combine(_paths.InstallDir, "Uninstall-AppPortalClient.ps1")));
    }

    [Fact]
    public async Task Users_are_allowed_to_leave_a_request_and_nothing_more()
    {
        var processes = new FakeProcesses();

        await Worker(new CountingFeed(), processes).AllowRequestsAsync(CancellationToken.None);

        var started = Assert.Single(processes.Started);
        Assert.Equal("icacls.exe", started.File);
        Assert.Contains("*S-1-5-32-545:(WD)", started.Arguments);
        // No inheritance flags: the grant is to add a file to the folder, not to touch the files in it.
        Assert.DoesNotContain("(OI)", started.Arguments);
        Assert.DoesNotContain("(CI)", started.Arguments);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The update worker did not get there in time.");
            }

            await Task.Delay(10);
        }
    }

    private void WriteInstallFile(string name) => File.WriteAllText(Path.Combine(_paths.InstallDir, name), "");

    private UpdateWorker Worker(
        IReleaseFeed feed,
        IProcessRunner processes,
        IUninstallRegistry? registry = null,
        ILogger<UpdateWorker>? logger = null)
    {
        var update = new SelfUpdate(feed, new UnusedDownloader(), processes, new ClosedClient(), _paths,
            new UpdateSignaturePolicy(new UnusedSignatures(), Path.Combine(_paths.InstallDir, "AppPortal.Agent.exe")),
            NullLogger<SelfUpdate>.Instance, () => Version.Parse("0.4.0.0"));
        // windows: false keeps schtasks and icacls out of the loop; both have tests that call them directly.
        return new UpdateWorker(update, _paths, processes, registry ?? new FakeRegistry(),
            logger ?? NullLogger<UpdateWorker>.Instance, TimeSpan.FromMilliseconds(20), windows: false);
    }

    /// <summary>Nothing here downloads an MSI, so nothing should ask who signed one.</summary>
    private sealed class UnusedSignatures : IFileSignatureReader
    {
        public FileSignature Read(string path) => throw new InvalidOperationException("No signature should be read.");
    }

    private sealed class CountingFeed : IReleaseFeed
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<ReleaseInfo?> GetLatestAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult<ReleaseInfo?>(null);
        }
    }

    private sealed class UnusedDownloader : IUpdateDownloader
    {
        public Task<string> FetchAsync(ReleaseInfo release, CancellationToken ct)
            => throw new InvalidOperationException("The feed has no newer release, so nothing should be downloaded.");
    }

    [Theory]
    [InlineData("AppPortalClient")]
    [InlineData("{8E0F9B3C-2A41-4D77-9C58-3B6E1F0A7D42}")]
    [InlineData("7-Zip 24.09 (x64)")]
    [InlineData("Python 3.12.1 (64-bit)")]
    public void A_real_uninstall_key_name_is_one_that_may_be_removed(string name)
        => Assert.True(UninstallKeyName.NamesOneKey(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"AppPortalClient\..\..")]
    [InlineData("/")]
    [InlineData("*")]
    public void A_name_that_reaches_wider_than_one_key_is_refused(string? name)
    {
        // A registry delete takes a path. A blank name is the Uninstall branch itself, and removing
        // that tree takes every entry on the PC with it. Today the only caller passes a constant; this
        // is what keeps the promise true of the next one.
        Assert.False(UninstallKeyName.NamesOneKey(name));
    }

    private sealed class ClosedClient : IClientPresence
    {
        public bool IsRunning() => false;
    }

    /// <summary>The uninstall entries a PC has, which a removal takes one of.</summary>
    private sealed class FakeRegistry(params string[] entries) : IUninstallRegistry
    {
        public List<string> Entries { get; } = [.. entries];

        public string? QuietUninstallString(string uninstallKey, string? account = null) => null;

        public bool Remove(string uninstallKey) => Entries.Remove(uninstallKey);
    }

    /// <summary>Everything the worker logged, so a test can say what is not in it.</summary>
    private sealed class RecordedLogs : ILogger<UpdateWorker>
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => [.. _entries];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private sealed class FakeProcesses(int exitCode = 0) : IProcessRunner
    {
        private readonly Lock _gate = new();

        public List<(string File, string Arguments)> Started { get; } = [];

        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            lock (_gate)
            {
                Started.Add((file, arguments));
            }

            return Task.FromResult(new ProcessResult(exitCode, ""));
        }
    }
}
