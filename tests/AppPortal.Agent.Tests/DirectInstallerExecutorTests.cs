using System.Security.Cryptography;

using AppPortal.Agent.Downloads;
using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Agent.Sessions;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

public sealed class DirectInstallerExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _body = RandomNumberGenerator.GetBytes(4096);

    public DirectInstallerExecutorTests() => Directory.CreateDirectory(_root);

    private DirectPackageDefinition Definition => new("https://vendor.example/app.exe",
        Convert.ToHexStringLower(SHA256.HashData(_body)), "exe", "/S", _body.Length);

    [Fact]
    public void An_exe_is_started_with_the_arguments_the_vendor_documented()
    {
        var (file, arguments) = DirectInstallerExecutor.Command(Definition, @"C:\cache\app.exe", _root, "job-1");

        Assert.Equal(@"C:\cache\app.exe", file);
        Assert.Equal("/S", arguments);
    }

    [Fact]
    public void An_msi_goes_through_windows_installer_with_a_log_of_its_own()
    {
        var (file, arguments) = DirectInstallerExecutor.Command(
            Definition with { InstallerType = "msi", SilentArgs = "/qn" }, @"C:\cache\app.msi", _root, "job-7");

        Assert.Equal("msiexec.exe", file);
        Assert.Contains(@"/i ""C:\cache\app.msi""", arguments);
        Assert.Contains("/qn", arguments);
        // Without /norestart an msi may restart the PC on its own, in the middle of somebody's day.
        Assert.Contains("/norestart", arguments);
        Assert.Contains("job-7.msi.log", arguments);
    }

    [Fact]
    public void An_msix_is_provisioned_rather_than_run_and_takes_no_arguments()
    {
        var (file, arguments) = DirectInstallerExecutor.Command(
            Definition with { InstallerType = "msix", SilentArgs = "" }, @"C:\cache\app.msix", _root, "job-1");

        Assert.Equal("powershell.exe", file);
        Assert.Contains("Add-AppxProvisionedPackage", arguments);
        Assert.Contains("-NonInteractive", arguments);
        Assert.Contains(@"app.msix", arguments);
    }

    [Theory]
    [InlineData(0, true, "Installed.")]
    [InlineData(3010, true, "Installed. This PC has to restart to finish.")]
    [InlineData(1641, true, "Installed. This PC has to restart to finish.")]
    public async Task Exit_codes_that_mean_it_worked_are_successes(int exitCode, bool ok, string detail)
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(exitCode, "")))
            .RunAsync(new JobContext("job-1", null), Definition, new Progress(), CancellationToken.None);

        Assert.Equal(ok, result.Ok);
        Assert.Equal(detail, result.Detail);
    }

    [Fact]
    public async Task A_failure_carries_the_end_of_what_the_installer_printed()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(1603, "Another installation is in progress")))
            .RunAsync(new JobContext("job-1", null), Definition, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(1603, result.ExitCode);
        Assert.Contains("Another installation is in progress", result.Detail);
    }

    [Fact]
    public async Task A_per_user_installer_runs_in_the_session_of_the_person_who_asked()
    {
        var sessions = new FakeSessions(@"CONTOSO\\ada");

        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")), sessions)
            .RunAsync(new JobContext("job-1", @"CONTOSO\\ada"), Definition with { Scope = "user" },
                new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        var started = Assert.Single(sessions.Started);
        Assert.Equal(@"CONTOSO\\ada", started.Account);
        Assert.Equal("/S", started.Arguments);
    }

    [Fact]
    public async Task A_per_user_installer_parks_when_that_person_is_not_signed_in()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")), new FakeSessions())
            .RunAsync(new JobContext("job-1", @"CONTOSO\\ada"), Definition with { Scope = "user" },
                new Progress(), CancellationToken.None);

        Assert.True(result.WaitingForUser);
        Assert.Equal(@"Waiting for CONTOSO\\ada to sign in.", result.Detail);
    }

    [Fact]
    public void A_per_user_msix_is_added_to_that_profile_rather_than_provisioned_for_later_ones()
    {
        // Provisioning only reaches profiles that do not exist yet, so it would never reach the person
        // who asked for it.
        var (file, arguments) = DirectInstallerExecutor.Command(
            Definition with { InstallerType = "msix", Scope = "user", SilentArgs = "" }, @"C:\cache\app.msix", _root, "job-1");

        Assert.Equal("powershell.exe", file);
        Assert.Contains("Add-AppxPackage", arguments);
        Assert.DoesNotContain("Add-AppxProvisionedPackage", arguments);
    }

    [Fact]
    public async Task A_job_that_is_not_a_direct_installer_says_so_rather_than_guessing()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")))
            .RunAsync(new JobContext("job-1", null), new WingetPackageDefinition("Valve.Steam", "machine"), new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("This job is not a direct installer.", result.Detail);
    }

    [Fact]
    public async Task Everything_the_installer_printed_is_kept_beside_the_job_id()
    {
        await Executor(new FakeProcesses((_, _) => new ProcessResult(0, ""), "Unpacking"))
            .RunAsync(new JobContext("job-9", null), Definition, new Progress(), CancellationToken.None);

        var log = File.ReadAllText(Path.Combine(_root, "jobs", "job-9.log"));
        Assert.Contains("Unpacking", log);
        Assert.Contains("exit 0", log);
    }

    [Fact]
    public void A_per_user_removal_asks_for_that_persons_copy_and_not_the_machines()
    {
        // The agent runs as LocalSystem, so its own HKEY_CURRENT_USER is the service account's. A
        // per-user application writes its uninstall entry into the profile of the person who installed
        // it, and a lookup that does not name them reads the machine and finds nothing.
        var registry = new FakeRegistry(@"C:\Users\ada\App\uninstall.exe /S");
        var command = DirectInstallerExecutor.UninstallCommand(
            Definition with { Scope = "user", UninstallKey = "TheApp" }, registry, @"CORP\ada");

        Assert.Equal(@"CORP\ada", Assert.Single(registry.Asked).Account);
        Assert.Equal("TheApp", Assert.Single(registry.Asked).Key);
        Assert.Equal(@"C:\Users\ada\App\uninstall.exe", command!.Value.File);
        Assert.Equal("/S", command.Value.Arguments);
    }

    [Fact]
    public void A_machine_wide_removal_names_nobody()
    {
        var registry = new FakeRegistry(@"""C:\Program Files\App\uninstall.exe"" --silent");
        var command = DirectInstallerExecutor.UninstallCommand(
            Definition with { UninstallKey = "TheApp" }, registry);

        Assert.Null(Assert.Single(registry.Asked).Account);
        Assert.Equal(@"C:\Program Files\App\uninstall.exe", command!.Value.File);
        Assert.Equal("--silent", command.Value.Arguments);
    }

    [Fact]
    public async Task A_per_user_removal_that_does_not_say_who_fails_before_it_reads_anything()
    {
        var registry = new FakeRegistry(@"C:\Users\ada\App\uninstall.exe /S");
        var executor = Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")), registry: registry);
        var result = await executor.UninstallAsync(new JobContext("job-1", null, InstallKind.Uninstall),
            Definition with { Scope = "user", UninstallKey = "TheApp" }, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("does not say who", result.Detail);
        Assert.Empty(registry.Asked);
    }

    [Fact]
    public async Task A_per_user_removal_runs_in_that_persons_session()
    {
        var registry = new FakeRegistry(@"C:\Users\ada\App\uninstall.exe /S");
        var sessions = new FakeSessions(@"CORP\ada");
        var executor = Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")), sessions, registry);
        var result = await executor.UninstallAsync(new JobContext("job-1", @"CORP\ada", InstallKind.Uninstall),
            Definition with { Scope = "user", UninstallKey = "TheApp" }, new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        var started = Assert.Single(sessions.Started);
        Assert.Equal(@"CORP\ada", started.Account);
        Assert.Equal(@"C:\Users\ada\App\uninstall.exe", started.File);
    }

    private DirectInstallerExecutor Executor(IProcessRunner processes, IUserSessionLauncher? sessions = null,
        IUninstallRegistry? registry = null)
    {
        var downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloads);
        var cache = new InstallerCache(downloads, NullLogger.Instance);
        // The installer is already in the cache under its own hash, so the executor finds it there and
        // the download path stays out of these tests; it has its own against a real server.
        File.WriteAllBytes(cache.PathFor(Definition.Sha256, "exe"), _body);
        return new DirectInstallerExecutor(
            new ResumableDownload(new HttpClient(), cache, NullLogger.Instance),
            processes, NullLogger<DirectInstallerExecutor>.Instance, _root, sessions ?? new FakeSessions(),
            registry ?? new NoUninstallRegistry());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Progress(Action<(int percent, string detail)>? report = null) : IProgress<(int percent, string detail)>
    {
        public void Report((int percent, string detail) value) => report?.Invoke(value);
    }

    /// <summary>Answers with one quiet command, and records what it was asked for.</summary>
    private sealed class FakeRegistry(string? quiet) : IUninstallRegistry
    {
        public List<(string Key, string? Account)> Asked { get; } = [];

        public string? QuietUninstallString(string uninstallKey, string? account = null)
        {
            Asked.Add((uninstallKey, account));
            return quiet;
        }

        public bool Remove(string uninstallKey) => false;
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
