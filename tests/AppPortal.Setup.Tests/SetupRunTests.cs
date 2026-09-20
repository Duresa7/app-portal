using AppPortal.Setup.Services;

namespace AppPortal.Setup.Tests;

/// <summary>
/// The whole job end to end, with the server, the package and msiexec all stood in for. What is proved
/// here is the order of the steps and the number the process comes back with.
/// </summary>
public sealed class SetupRunTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly MsiInstallRequest Request =
        new("https://portal.example.internal", "ape_ABC", null);

    [Fact]
    public async Task A_run_that_installs_and_enrolls_returns_zero_and_the_name_the_server_gave()
    {
        using var world = new World();
        world.Probe.DeviceName = "PC-RENAMED";
        world.EnrollOnInstall();

        var outcome = await world.Run().RunAsync(Request, null, CancellationToken.None);

        Assert.Equal(ExitCodes.Success, outcome.ExitCode);
        Assert.Equal("PC-RENAMED", outcome.DeviceName);
        Assert.Null(outcome.Problem);
    }

    [Fact]
    public async Task A_key_the_server_refuses_stops_before_anything_is_installed()
    {
        using var world = new World();
        world.Probe.Key = ProbeResult.Bad("That enrollment key is not usable.");

        var outcome = await world.Run().RunAsync(Request, null, CancellationToken.None);

        Assert.Equal(ExitCodes.EnrollmentFailed, outcome.ExitCode);
        Assert.Equal("That enrollment key is not usable.", outcome.Problem);
        Assert.Empty(world.Processes.Commands);
        Assert.Null(outcome.LogPath);
    }

    [Fact]
    public async Task A_failed_install_comes_back_as_the_code_msiexec_gave_and_names_the_log()
    {
        using var world = new World();
        world.Processes.ExitCode = 1618;

        var outcome = await world.Run().RunAsync(Request, null, CancellationToken.None);

        Assert.Equal(1618, outcome.ExitCode);
        Assert.Contains("Another installation is already running", outcome.Problem);
        Assert.NotNull(outcome.LogPath);
    }

    [Fact]
    public async Task An_install_that_lands_but_never_enrolls_is_an_enrollment_failure()
    {
        using var world = new World();

        var outcome = await world.Run().RunAsync(Request, null, CancellationToken.None);

        Assert.Equal(ExitCodes.EnrollmentFailed, outcome.ExitCode);
        Assert.Contains("did not enroll", outcome.Problem);
    }

    [Fact]
    public async Task An_install_that_asks_for_a_restart_keeps_saying_so_after_it_enrolls()
    {
        using var world = new World();
        world.Processes.ExitCode = ExitCodes.RestartRequired;
        world.EnrollOnInstall();

        var outcome = await world.Run().RunAsync(Request, null, CancellationToken.None);

        Assert.Equal(ExitCodes.RestartRequired, outcome.ExitCode);
        Assert.True(outcome.Ok);
        Assert.True(outcome.RestartRequired);
    }

    [Fact]
    public async Task A_build_with_no_package_inside_it_installs_nothing_and_says_why()
    {
        using var world = new World { Package = new FakeMsiSource(present: false) };

        var outcome = await world.Run().RunAsync(Request, null, CancellationToken.None);

        Assert.Equal(ExitCodes.PackageUnavailable, outcome.ExitCode);
        Assert.Contains("built without the App Portal installer", outcome.Problem);
        Assert.Empty(world.Processes.Commands);
    }

    [Fact]
    public async Task The_package_it_unpacked_is_gone_again_afterwards()
    {
        using var world = new World();
        var package = new FakeMsiSource();
        world.Package = package;
        world.EnrollOnInstall();

        await world.Run().RunAsync(Request, null, CancellationToken.None);

        Assert.NotNull(package.Extracted);
        Assert.False(File.Exists(package.Extracted));
        Assert.False(Directory.Exists(world.Scratch));
    }

    [Fact]
    public async Task Progress_walks_the_stages_in_order()
    {
        using var world = new World();
        world.EnrollOnInstall();
        var seen = new List<SetupProgress>();

        await world.Run().RunAsync(Request, new RecordingProgress<SetupProgress>(seen), CancellationToken.None);

        Assert.Equal(SetupStage.Checking, seen[0].Stage);
        Assert.Contains(seen, step => step.Stage == SetupStage.Installing);
        Assert.Contains(seen, step => step.Stage == SetupStage.Enrolling);
        Assert.Equal(SetupStage.Finished, seen[^1].Stage);
        Assert.Equal(100, seen[^1].Percent);
    }

    /// <summary>Everything a run touches, stood in for, with the folders cleaned up afterwards.</summary>
    private sealed class World : IDisposable
    {
        private readonly TemporaryFolder _data = new();

        public World()
        {
            Scratch = Path.Combine(Path.GetTempPath(), "app-portal-setup-scratch-" + Guid.NewGuid().ToString("N")[..8]);
        }

        public FakeProbe Probe { get; } = new();

        public FakeProcessRunner Processes { get; } = new();

        public IMsiSource Package { get; set; } = new FakeMsiSource();

        public FakeClock Clock { get; } = new(Start);

        public string Scratch { get; }

        /// <summary>Pretends the agent enrolled while msiexec was running, as it does on a real PC.</summary>
        public void EnrollOnInstall()
            => Processes.WhileRunning = _ =>
            {
                _data.Write("client.json", """{"serverUrl":"https://portal.example.internal","deviceToken":"apd_token"}""");
                _data.Write("agent.json", $$"""{"lastHeartbeatAt":"{{Start:O}}","outcome":"Succeeded"}""");
            };

        public SetupRun Run() => new(
            Package,
            Probe,
            new MsiInstall(Processes),
            new EnrollmentWatcher(_data.Path, Clock.Read, Clock.WaitAsync),
            Clock.Read,
            () => Scratch);

        public void Dispose()
        {
            _data.Dispose();
            if (Directory.Exists(Scratch))
            {
                Directory.Delete(Scratch, recursive: true);
            }
        }
    }
}
