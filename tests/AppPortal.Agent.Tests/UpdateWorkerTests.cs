using AppPortal.Agent.Jobs;
using AppPortal.Agent.Update;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

public sealed class UpdateWorkerTests : IDisposable
{
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

    private UpdateWorker Worker(IReleaseFeed feed, IProcessRunner processes)
    {
        var update = new SelfUpdate(feed, new UnusedDownloader(), processes, new ClosedClient(), _paths,
            NullLogger<SelfUpdate>.Instance, () => Version.Parse("0.4.0.0"));
        // windows: false keeps schtasks and icacls out of the loop; both have tests that call them directly.
        return new UpdateWorker(update, _paths, processes, NullLogger<UpdateWorker>.Instance,
            TimeSpan.FromMilliseconds(20), windows: false);
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

    private sealed class ClosedClient : IClientPresence
    {
        public bool IsRunning() => false;
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
