using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

public sealed class ManagedPackageExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-managed-tests", Guid.NewGuid().ToString("N"));

    public ManagedPackageExecutorTests() => Directory.CreateDirectory(_root);

    private static readonly ManagedPackageDefinition Choco = new("choco", "7zip", "machine");

    private static readonly ManagedPackageDefinition Scoop = new("scoop", "7zip", "user");

    [Fact]
    public async Task A_machine_wide_package_runs_the_located_manager_as_system()
    {
        var processes = new FakeProcesses((_, _) => new ProcessResult(0, ""));
        var result = await Executor(processes).RunAsync(new JobContext("job-1", null), Choco, new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Installed.", result.Detail);
        var (file, arguments) = Assert.Single(processes.Started);
        Assert.Equal(@"C:\found\choco.exe", file);
        Assert.Equal("install 7zip -y --no-progress --limit-output", arguments);
    }

    [Fact]
    public async Task A_per_user_package_runs_in_that_persons_session_with_their_copy_of_the_manager()
    {
        var sessions = new FakeSessions(@"CORP\ada");
        var locator = new FakeLocator(@"C:\found\scoop.cmd");
        var processes = new FakeProcesses((_, _) => throw new InvalidOperationException("Must not run as SYSTEM."));
        var result = await Executor(processes, sessions, locator)
            .RunAsync(new JobContext("job-1", @"CORP\ada"), Scoop, new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(@"CORP\ada", Assert.Single(locator.Asked).Account);
        var started = Assert.Single(sessions.Started);
        Assert.Equal(@"CORP\ada", started.Account);
        Assert.Equal("install 7zip", started.Arguments);
    }

    [Fact]
    public async Task A_per_user_package_parks_until_the_person_signs_in()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")), new FakeSessions())
            .RunAsync(new JobContext("job-1", @"CORP\ada"), Scoop, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(result.WaitingForUser);
        Assert.Equal(@"Waiting for CORP\ada to sign in.", result.Detail);
    }

    [Fact]
    public async Task A_per_user_package_that_does_not_say_who_fails_before_looking_for_anything()
    {
        var locator = new FakeLocator(@"C:\found\scoop.cmd");
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")), locator: locator)
            .RunAsync(new JobContext("job-1", null), Scoop, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.WaitingForUser);
        Assert.Empty(locator.Asked);
    }

    [Fact]
    public async Task A_missing_manager_is_named_and_points_at_the_prerequisite_mechanism()
    {
        var processes = new FakeProcesses((_, _) => new ProcessResult(0, ""));
        var result = await Executor(processes, locator: new FakeLocator(null))
            .RunAsync(new JobContext("job-1", null), Choco, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Chocolatey is not installed on this PC", result.Detail);
        Assert.Contains("prerequisite", result.Detail);
        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task A_manager_this_agent_has_never_heard_of_says_so_once()
    {
        // A newer server can describe a manager this agent predates. Saying so is a fact to report,
        // not a failure to retry three times.
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")))
            .RunAsync(new JobContext("job-1", null), new ManagedPackageDefinition("apt", "curl", "machine"), new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("This agent does not know the package manager apt. It is likely older than the server.", result.Detail);
    }

    [Fact]
    public async Task A_failure_names_the_manager_the_package_and_the_exit_code()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(1, "Chocolatey installed 0/1 packages.\n7zip not found")))
            .RunAsync(new JobContext("job-1", null), Choco, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("Chocolatey could not install 7zip (exit code 1).", result.Detail);
        Assert.Contains("7zip not found", result.Detail);
    }

    [Fact]
    public async Task A_restart_code_finishes_the_install_and_asks_for_the_restart()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(3010, "")))
            .RunAsync(new JobContext("job-1", null), Choco, new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.True(result.NeedsRestart);
    }

    [Fact]
    public async Task Removing_a_chocolatey_package_that_is_already_gone_is_not_a_failure()
    {
        // A retried job, or somebody who removed it by hand first: the PC is already as asked.
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(1605, "")))
            .UninstallAsync(new JobContext("job-1", null), Choco, new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("It was not installed.", result.Detail);
    }

    [Fact]
    public async Task A_manager_that_runs_too_long_fails_with_a_sentence_rather_than_a_crash()
    {
        var result = await Executor(new FakeProcesses((_, _) => throw new TimeoutException("choco.exe did not finish within 60 minutes.")))
            .RunAsync(new JobContext("job-1", null), Choco, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("choco.exe did not finish within 60 minutes.", result.Detail);
    }

    [Fact]
    public async Task Removal_runs_the_uninstall_command_from_the_same_row()
    {
        var processes = new FakeProcesses((_, _) => new ProcessResult(0, ""));
        var result = await Executor(processes)
            .UninstallAsync(new JobContext("job-1", null, InstallKind.Uninstall), Choco, new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Removed.", result.Detail);
        Assert.Equal("uninstall 7zip -y --limit-output", Assert.Single(processes.Started).Arguments);
    }

    [Fact]
    public async Task Progress_reads_as_downloading_only_while_the_manager_says_it_is()
    {
        // The job runner turns a detail beginning with Downloading into the downloading state, so this
        // word has to come from the manager and not from us.
        var reported = new List<string>();
        var processes = new FakeProcesses((_, _) => new ProcessResult(0, ""), "Downloading 7zip 40%", "Installing 7zip");
        await Executor(processes).RunAsync(new JobContext("job-1", null), Choco,
            new Progress(p => reported.Add(p.detail)), CancellationToken.None);

        Assert.Equal(["Installing", "Downloading 40%", "Installing"], reported);
    }

    private ManagedPackageExecutor Executor(FakeProcesses processes, FakeSessions? sessions = null, FakeLocator? locator = null)
        => new(processes, sessions ?? new FakeSessions(), locator ?? new FakeLocator(@"C:\found\choco.exe"),
            NullLogger<ManagedPackageExecutor>.Instance, _root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeLocator(string? path) : IPackageManagerLocator
    {
        public List<(string Manager, string? Account)> Asked { get; } = [];

        public string? Find(PackageManagerDescriptor manager, string? account)
        {
            Asked.Add((manager.Name, account));
            return path;
        }
    }

    private sealed class Progress(Action<(int percent, string detail)>? report = null) : IProgress<(int percent, string detail)>
    {
        public void Report((int percent, string detail) value) => report?.Invoke(value);
    }

    private sealed class FakeProcesses(Func<string, string, ProcessResult> run, params string[] lines) : IProcessRunner
    {
        public List<(string File, string Arguments)> Started { get; } = [];

        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            Started.Add((file, arguments));
            foreach (var line in lines)
            {
                onLine?.Invoke(line);
            }

            return Task.FromResult(run(file, arguments));
        }
    }
}
