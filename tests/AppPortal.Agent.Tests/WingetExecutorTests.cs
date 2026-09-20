using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

public sealed class WingetExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));

    public WingetExecutorTests() => Directory.CreateDirectory(_root);

    private static WingetPackageDefinition Package => new("Valve.Steam", "machine");

    [Fact]
    public async Task A_machine_scope_install_runs_winget_with_the_arguments_that_make_it_silent()
    {
        string? command = null;
        var executor = Executor(new FakeProcesses((file, arguments) =>
        {
            command = arguments;
            return new ProcessResult(0, "Successfully installed");
        }));

        var result = await executor.RunAsync("job-1", Package, new Progress(), CancellationToken.None);

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
    [InlineData(3010, true, "Installed. Restart required.")]
    [InlineData(1641, true, "Installed. Restart required.")]
    public async Task Exit_codes_that_mean_nothing_to_do_are_not_failures(int exitCode, bool ok, string detail)
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(exitCode, "")))
            .RunAsync("job-1", Package, new Progress(), CancellationToken.None);

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
            .RunAsync("job-1", Package, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("No installer for Valve.Steam matches this PC at machine scope.", result.Detail);
    }

    [Fact]
    public async Task An_unknown_failure_carries_the_end_of_the_output()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(1, "resolving\nInstaller hash does not match")))
            .RunAsync("job-1", Package, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("exit code 1", result.Detail);
        Assert.Contains("Installer hash does not match", result.Detail);
    }

    [Fact]
    public async Task A_per_user_package_is_refused_in_words_rather_than_installed_for_nobody()
    {
        var ran = false;
        var result = await Executor(new FakeProcesses((_, _) =>
            {
                ran = true;
                return new ProcessResult(0, "");
            }))
            .RunAsync("job-1", Package with { Scope = "user" }, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("cannot yet run in a user session", result.Detail);
        Assert.False(ran);
    }

    [Fact]
    public async Task A_missing_winget_says_so_instead_of_failing_obscurely()
    {
        var executor = new WingetExecutor(new FakeProcesses((_, _) => new ProcessResult(0, "")),
            NullLogger<WingetExecutor>.Instance, _root, new WingetLocator(Path.Combine(_root, "absent")));

        var result = await executor.RunAsync("job-1", Package, new Progress(), CancellationToken.None);

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
        await executor.RunAsync("job-1", Package, new Progress(), CancellationToken.None);
        await executor.RunAsync("job-2", Package, new Progress(), CancellationToken.None);
        Assert.Equal(1, updates);

        File.SetLastWriteTimeUtc(Path.Combine(_root, "winget-source-updated"), DateTime.UtcNow.AddDays(-2));
        await executor.RunAsync("job-3", Package, new Progress(), CancellationToken.None);
        Assert.Equal(2, updates);
    }

    [Fact]
    public async Task Everything_the_run_printed_is_kept_beside_the_job_id()
    {
        await Executor(new FakeProcesses((_, _) => new ProcessResult(0, ""), "Downloading 40%"))
            .RunAsync("job-42", Package, new Progress(), CancellationToken.None);

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
            .RunAsync("job-1", Package, progress, CancellationToken.None);

        Assert.Contains((43, "Downloading 43%"), reports);
        Assert.Contains(reports, r => r.detail == "Installing");
    }

    private WingetExecutor Executor(IProcessRunner processes)
    {
        // A directory shaped like the real WindowsApps, so the locator has something to find.
        var apps = Path.Combine(_root, "WindowsApps", "Microsoft.DesktopAppInstaller_1.22.0.0_x64__8wekyb3d8bbwe");
        Directory.CreateDirectory(apps);
        File.WriteAllText(Path.Combine(apps, "winget.exe"), "");
        return new WingetExecutor(processes, NullLogger<WingetExecutor>.Instance, _root,
            new WingetLocator(Path.Combine(_root, "WindowsApps")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Progress(Action<(int percent, string detail)>? report = null) : IProgress<(int percent, string detail)>
    {
        public void Report((int percent, string detail) value) => report?.Invoke(value);
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
