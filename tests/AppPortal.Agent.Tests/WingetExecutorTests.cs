using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Agent.Sessions;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

public sealed class WingetExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));

    public WingetExecutorTests() => Directory.CreateDirectory(_root);

    private static WingetPackageDefinition Package => new("Valve.Steam", "machine");

    [Fact]
    public void A_winget_package_names_the_winget_source()
    {
        var arguments = WingetExecutor.Arguments(new WingetPackageDefinition("Valve.Steam", "machine"));

        Assert.Contains("--source winget", arguments);
        Assert.DoesNotContain("msstore", arguments);
    }

    [Fact]
    public void A_store_package_names_the_store_source()
    {
        // The Store is a winget source, not a second mechanism. Naming it is the whole change: without
        // --source, winget resolves a Store product id against winget-pkgs and reports that no
        // installer matches this PC, which reads like a packaging fault rather than a wrong source.
        var arguments = WingetExecutor.Arguments(
            new WingetPackageDefinition("9WZDNCRFJ3TJ", "user", Source: WingetSources.Store));

        Assert.Contains("--source msstore", arguments);
        Assert.Contains("--id 9WZDNCRFJ3TJ", arguments);
        Assert.Contains("--scope user", arguments);
    }

    [Fact]
    public async Task A_machine_scope_install_runs_winget_with_the_arguments_that_make_it_silent()
    {
        string? command = null;
        var executor = Executor(new FakeProcesses((file, arguments) =>
        {
            command = arguments;
            return new ProcessResult(0, "Successfully installed");
        }));

        var result = await executor.RunAsync(new JobContext("job-1", null), Package, new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.NotNull(command);
        // Every one of these matters: without them winget waits for somebody to answer a prompt that
        // nobody will ever see, because it is running as a service with no desktop.
        Assert.Contains("install --id Valve.Steam --exact", command);
        Assert.Contains("--scope machine", command);
        Assert.Contains("--silent", command);
        Assert.Contains("--accept-package-agreements", command);
        Assert.Contains("--accept-source-agreements", command);
        Assert.Contains("--disable-interactivity", command);
    }

    [Fact]
    public void The_scope_comes_from_the_definition_and_a_version_is_pinned_when_given()
    {
        Assert.Contains("--scope user", WingetExecutor.Arguments(Package with { Scope = "user" }));
        Assert.Contains("--version 1.2.3", WingetExecutor.Arguments(Package with { Version = "1.2.3" }));
        Assert.DoesNotContain("--version", WingetExecutor.Arguments(Package));
        Assert.EndsWith("--custom /NORESTART", WingetExecutor.Arguments(Package with { ExtraArgs = "--custom /NORESTART" }));
    }

    [Theory]
    [InlineData(0, true, "Installed.")]
    [InlineData(unchecked((int)0x8A150011), true, "Already installed.")]
    [InlineData(3010, true, "Installed. This PC has to restart to finish.")]
    [InlineData(1641, true, "Installed. This PC has to restart to finish.")]
    public async Task Exit_codes_that_mean_nothing_to_do_are_not_failures(int exitCode, bool ok, string detail)
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(exitCode, "")))
            .RunAsync(new JobContext("job-1", null), Package, new Progress(), CancellationToken.None);

        Assert.Equal(ok, result.Ok);
        Assert.Equal(detail, result.Detail);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Fact]
    public async Task No_applicable_installer_says_which_scope_was_asked_for()
    {
        // This is what a per-user package answers a machine-scope install, and the raw code tells the
        // administrator nothing about what to change.
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(unchecked((int)0x8A15002B), "")))
            .RunAsync(new JobContext("job-1", null), Package, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("No installer for Valve.Steam matches this PC at machine scope.", result.Detail);
    }

    [Fact]
    public async Task An_unknown_failure_carries_the_end_of_the_output()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(1, "resolving\nInstaller hash does not match")))
            .RunAsync(new JobContext("job-1", null), Package, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("exit code 1", result.Detail);
        Assert.Contains("Installer hash does not match", result.Detail);
    }

    [Fact]
    public async Task A_per_user_package_runs_in_the_session_of_the_person_who_asked()
    {
        var asService = new List<string>();
        var sessions = new FakeSessions(@"CONTOSO\\ada");
        var result = await Executor(new FakeProcesses((_, arguments) =>
            {
                asService.Add(arguments);
                return new ProcessResult(0, "");
            }), sessions)
            .RunAsync(new JobContext("job-1", @"CONTOSO\\ada"), Package with { Scope = "user" }, new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        // As her, never as the service account, whose profile she would never find the software in.
        // Refreshing the package index still runs as the service, which is right; installing does not.
        Assert.DoesNotContain(asService, arguments => arguments.StartsWith("install"));
        var started = Assert.Single(sessions.Started);
        Assert.Equal(@"CONTOSO\\ada", started.Account);
        Assert.Contains("--scope user", started.Arguments);
    }

    [Fact]
    public async Task A_per_user_package_parks_when_the_person_who_asked_is_not_signed_in()
    {
        // Somebody else being at the PC is not good enough: the software would land in their profile.
        var sessions = new FakeSessions(@"CONTOSO\\bob");

        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")), sessions)
            .RunAsync(new JobContext("job-1", @"CONTOSO\\ada"), Package with { Scope = "user" }, new Progress(), CancellationToken.None);

        Assert.True(result.WaitingForUser);
        Assert.False(result.Ok);
        Assert.Equal(@"Waiting for CONTOSO\\ada to sign in.", result.Detail);
        Assert.Empty(sessions.Started);
    }

    [Fact]
    public async Task A_per_user_package_with_nobody_named_says_so_rather_than_guessing()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")), new FakeSessions(@"CONTOSO\\ada"))
            .RunAsync(new JobContext("job-1", null), Package with { Scope = "user" }, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.WaitingForUser);
        Assert.Contains("does not say who asked", result.Detail);
    }

    [Fact]
    public async Task A_missing_winget_says_so_instead_of_failing_obscurely()
    {
        var executor = new WingetExecutor(new FakeProcesses((_, _) => new ProcessResult(0, "")),
            NullLogger<WingetExecutor>.Instance, _root, new FakeSessions(), new WingetLocator(Path.Combine(_root, "absent")));

        var result = await executor.RunAsync(new JobContext("job-1", null), Package, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("winget is not installed", result.Detail);
    }

    [Fact]
    public async Task The_sources_refresh_once_a_day_rather_than_once_an_install()
    {
        var updates = 0;
        var processes = new FakeProcesses((_, arguments) =>
        {
            if (arguments.StartsWith("source update"))
            {
                updates++;
            }

            return new ProcessResult(0, "");
        });

        var executor = Executor(processes);
        await executor.RunAsync(new JobContext("job-1", null), Package, new Progress(), CancellationToken.None);
        await executor.RunAsync(new JobContext("job-2", null), Package, new Progress(), CancellationToken.None);
        Assert.Equal(1, updates);

        File.SetLastWriteTimeUtc(Path.Combine(_root, "winget-source-updated"), DateTime.UtcNow.AddDays(-2));
        await executor.RunAsync(new JobContext("job-3", null), Package, new Progress(), CancellationToken.None);
        Assert.Equal(2, updates);
    }

    [Fact]
    public async Task Everything_the_run_printed_is_kept_beside_the_job_id()
    {
        await Executor(new FakeProcesses((_, _) => new ProcessResult(0, ""), "Downloading 40%"))
            .RunAsync(new JobContext("job-42", null), Package, new Progress(), CancellationToken.None);

        var log = File.ReadAllText(Path.Combine(_root, "jobs", "job-42.log"));
        Assert.Contains("Downloading 40%", log);
        Assert.Contains("exit 0", log);
    }

    [Fact]
    public async Task Progress_reaches_the_caller_as_winget_prints_it()
    {
        var reports = new List<(int percent, string detail)>();
        var progress = new Progress(reports.Add);

        await Executor(new FakeProcesses((_, _) => new ProcessResult(0, ""), "Downloading  43.0%", "Starting package install..."))
            .RunAsync(new JobContext("job-1", null), Package, progress, CancellationToken.None);

        Assert.Contains((43, "Downloading 43%"), reports);
        Assert.Contains(reports, r => r.detail == "Installing");
    }

    [Fact]
    public async Task Winget_starts_with_its_framework_packages_ahead_of_path_for_both_scopes()
    {
        // Started by its path as SYSTEM, or as an account the App Installer is not yet registered to,
        // winget.exe finds none of its framework DLLs and exits with STATUS_DLL_NOT_FOUND. Every launch
        // has to carry them, the source update included.
        var processes = new PathRecordingProcesses();
        var sessions = new PathRecordingSessions("PC\\alice");
        var executor = Executor(processes, sessions);
        var apps = Path.Combine(_root, "WindowsApps");
        File.WriteAllText(Path.Combine(apps, "Microsoft.DesktopAppInstaller_1.22.0.0_x64__8wekyb3d8bbwe", "AppxManifest.xml"), """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Microsoft.DesktopAppInstaller" ProcessorArchitecture="x64" />
              <Dependencies><PackageDependency Name="Microsoft.VCLibs.140.00.UWPDesktop" /></Dependencies>
            </Package>
            """);
        var vclibs = Path.Combine(apps, "Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64__8wekyb3d8bbwe");
        Directory.CreateDirectory(vclibs);

        await executor.RunAsync(new JobContext("job-1", null), Package, new Progress(), CancellationToken.None);
        await executor.RunAsync(new JobContext("job-2", "PC\\alice"), Package with { Scope = "user" }, new Progress(), CancellationToken.None);

        Assert.Equal(2, processes.PathFirst.Count);
        Assert.All(processes.PathFirst, first => Assert.Equal<string>([vclibs], first));
        Assert.Equal<string>([vclibs], Assert.Single(sessions.PathFirst));
    }

    private WingetExecutor Executor(IProcessRunner processes, IUserSessionLauncher? sessions = null)
    {
        // A directory shaped like the real WindowsApps, so the locator has something to find.
        var apps = Path.Combine(_root, "WindowsApps", "Microsoft.DesktopAppInstaller_1.22.0.0_x64__8wekyb3d8bbwe");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps, "winget.exe"), "");
        return new WingetExecutor(processes, NullLogger<WingetExecutor>.Instance, _root,
            sessions ?? new FakeSessions(), new WingetLocator(Path.Combine(_root, "WindowsApps")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Progress(Action<(int percent, string detail)>? report = null) : IProgress<(int percent, string detail)>
    {
        public void Report((int percent, string detail) value) => report?.Invoke(value);
    }

    private sealed class PathRecordingProcesses : IProcessRunner
    {
        public List<IReadOnlyList<string>> PathFirst { get; } = [];

        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
            => throw new InvalidOperationException("winget was started without its framework packages.");

        public Task<ProcessResult> RunAsync(string file, string arguments, IReadOnlyList<string> pathFirst, Action<string>? onLine,
            TimeSpan timeout, CancellationToken ct)
        {
            PathFirst.Add(pathFirst);
            return Task.FromResult(new ProcessResult(0, ""));
        }
    }

    private sealed class PathRecordingSessions(params string[] signedIn) : IUserSessionLauncher
    {
        public List<IReadOnlyList<string>> PathFirst { get; } = [];

        public IReadOnlyList<string> SignedInAccounts() => signedIn;

        public Task<ProcessResult?> RunAsAsync(string account, string file, string arguments, Action<string>? onLine,
            TimeSpan timeout, CancellationToken ct)
            => throw new InvalidOperationException("winget was started without its framework packages.");

        public Task<ProcessResult?> RunAsAsync(string account, string file, string arguments, IReadOnlyList<string> pathFirst,
            Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            PathFirst.Add(pathFirst);
            return Task.FromResult<ProcessResult?>(new ProcessResult(0, ""));
        }
    }

    private sealed class FakeProcesses(Func<string, string, ProcessResult> run, params string[] lines) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            foreach (var line in lines)
            {
                onLine?.Invoke(line);
            }

            return Task.FromResult(run(file, arguments));
        }
    }
}
