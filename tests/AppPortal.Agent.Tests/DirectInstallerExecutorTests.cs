using System.Security.Cryptography;

using AppPortal.Agent.Downloads;
using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
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
    [InlineData(3010, true, "Installed. Restart required.")]
    [InlineData(1641, true, "Installed. Restart required.")]
    public async Task Exit_codes_that_mean_it_worked_are_successes(int exitCode, bool ok, string detail)
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(exitCode, "")))
            .RunAsync("job-1", Definition, new Progress(), CancellationToken.None);

        Assert.Equal(ok, result.Ok);
        Assert.Equal(detail, result.Detail);
    }

    [Fact]
    public async Task A_failure_carries_the_end_of_what_the_installer_printed()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(1603, "Another installation is in progress")))
            .RunAsync("job-1", Definition, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(1603, result.ExitCode);
        Assert.Contains("Another installation is in progress", result.Detail);
    }

    [Fact]
    public async Task A_per_user_package_is_refused_before_anything_is_downloaded()
    {
        var ran = false;
        // A URL nothing serves and a hash nothing matches: reaching the download would fail with a
        // different message, so the refusal below can only have come first.
        var unreachable = Definition with { Scope = "user", Url = "http://127.0.0.1:1/app.exe", Sha256 = new string('b', 64) };

        var result = await Executor(new FakeProcesses((_, _) =>
            {
                ran = true;
                return new ProcessResult(0, "");
            }))
            .RunAsync("job-1", unreachable, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("cannot yet run in a user session", result.Detail);
        Assert.False(ran);
    }

    [Fact]
    public async Task A_job_that_is_not_a_direct_installer_says_so_rather_than_guessing()
    {
        var result = await Executor(new FakeProcesses((_, _) => new ProcessResult(0, "")))
            .RunAsync("job-1", new WingetPackageDefinition("Valve.Steam", "machine"), new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("This job is not a direct installer.", result.Detail);
    }

    [Fact]
    public async Task Everything_the_installer_printed_is_kept_beside_the_job_id()
    {
        await Executor(new FakeProcesses((_, _) => new ProcessResult(0, ""), "Unpacking"))
            .RunAsync("job-9", Definition, new Progress(), CancellationToken.None);

        var log = File.ReadAllText(Path.Combine(_root, "jobs", "job-9.log"));
        Assert.Contains("Unpacking", log);
        Assert.Contains("exit 0", log);
    }

    private DirectInstallerExecutor Executor(IProcessRunner processes)
    {
        var downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloads);
        var cache = new InstallerCache(downloads, NullLogger.Instance);
        // The installer is already in the cache under its own hash, so the executor finds it there and
        // the download path stays out of these tests; it has its own against a real server.
        File.WriteAllBytes(cache.PathFor(Definition.Sha256, "exe"), _body);
        return new DirectInstallerExecutor(
            new ResumableDownload(new HttpClient(), cache, NullLogger.Instance),
            processes, NullLogger<DirectInstallerExecutor>.Instance, _root);
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
