using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

using AppPortal.Agent.Downloads;
using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Agent.Sessions;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace AppPortal.Agent.Tests;

/// <summary>
/// A zip unpacked into a folder, with a shortcut and an uninstall entry. The script is where the work
/// is, so most of these read it; the Windows-only ones run it for real against the current account.
/// </summary>
public sealed class PortableAppExecutorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
    private readonly byte[] _body = RandomNumberGenerator.GetBytes(2048);

    public PortableAppExecutorTests() => Directory.CreateDirectory(_root);

    private PortablePackageDefinition Definition => new("https://vendor.example/tool.zip",
        Convert.ToHexStringLower(SHA256.HashData(_body)), _body.Length, "Vendor Tool", @"bin\tool.exe",
        ShortcutName: "Vendor Tool", Version: "2.1");

    [Fact]
    public void A_machine_wide_app_goes_under_program_files_with_a_shortcut_for_everyone()
    {
        var script = PortableAppExecutor.InstallScript(Definition, @"C:\ProgramData\AppPortal\downloads\abc.zip");

        Assert.Contains("$root = Join-Path $env:ProgramFiles 'App Portal Apps'", script);
        Assert.Contains(@"$menu = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'", script);
        Assert.Contains(@"$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppPortalPortable-' + $folder", script);
        Assert.Contains("$folder = 'Vendor Tool'", script);
        Assert.Contains(@"$executable = 'bin\tool.exe'", script);
        Assert.Contains(@"$zip = 'C:\ProgramData\AppPortal\downloads\abc.zip'", script);
        Assert.Contains("$version = '2.1'", script);
    }

    [Fact]
    public void A_per_user_app_goes_into_the_profile_of_whoever_runs_the_script()
    {
        var script = PortableAppExecutor.InstallScript(Definition with { Scope = "user" }, @"C:\cache\abc.zip");

        Assert.Contains(@"$root = Join-Path $env:LOCALAPPDATA 'Programs\App Portal Apps'", script);
        Assert.Contains(@"$menu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'", script);
        Assert.Contains(@"'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppPortalPortable-'", script);
        Assert.DoesNotContain("HKLM:", script);
    }

    [Fact]
    public void The_path_to_the_program_is_written_with_backslashes()
    {
        var script = PortableAppExecutor.InstallScript(Definition with { Executable = "bin/x64/tool.exe" }, @"C:\cache\abc.zip");

        Assert.Contains(@"$executable = 'bin\x64\tool.exe'", script);
    }

    [Fact]
    public void A_quote_in_a_value_cannot_end_its_literal()
    {
        Assert.Equal("'C:\\it''s\\a.zip'", PortableAppExecutor.Literal(@"C:\it's\a.zip"));
    }

    [Fact]
    public void The_install_leaves_a_removal_script_for_settings_to_run()
    {
        var script = PortableAppExecutor.InstallScript(Definition, @"C:\cache\abc.zip");

        Assert.Contains("Set-Content -LiteralPath $removal -Encoding ASCII -Value @'", script);
        Assert.Contains("QuietUninstallString", script);
        // The here-string ends on a line of its own, or PowerShell reads the rest of the script into it.
        Assert.Contains("\n'@\n", script.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_machine_wide_app_is_unpacked_as_system()
    {
        var processes = new FakeProcesses(_ => new ProcessResult(0, ""));
        var sessions = new FakeSessions();

        var result = await Executor(processes, sessions).RunAsync(new JobContext("job-1", null), Definition, new Progress(),
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Installed.", result.Detail);
        var (file, arguments) = Assert.Single(processes.Started);
        Assert.Equal("powershell.exe", file);
        Assert.Contains("-ExecutionPolicy Bypass -File", arguments);
        Assert.Contains("job-1.portable.ps1", arguments);
        Assert.Empty(sessions.Started);
        Assert.Contains("$ErrorActionPreference = 'Stop'", File.ReadAllText(Path.Combine(_root, "jobs", "job-1.portable.ps1")));
    }

    [Fact]
    public async Task A_per_user_app_is_unpacked_in_the_session_of_the_person_who_asked()
    {
        var processes = new FakeProcesses(_ => throw new InvalidOperationException("Not as SYSTEM."));
        var sessions = new FakeSessions(@"CORP\ada");

        var result = await Executor(processes, sessions).RunAsync(new JobContext("job-2", @"CORP\ada"),
            Definition with { Scope = "user" }, new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        var started = Assert.Single(sessions.Started);
        Assert.Equal(@"CORP\ada", started.Account);
        Assert.Equal("powershell.exe", started.File);
    }

    [Fact]
    public async Task A_per_user_app_waits_for_its_person_to_sign_in()
    {
        var result = await Executor(new FakeProcesses(_ => new ProcessResult(0, "")), new FakeSessions())
            .RunAsync(new JobContext("job-3", @"CORP\ada"), Definition with { Scope = "user" }, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(result.WaitingForUser);
        Assert.Equal(@"Waiting for CORP\ada to sign in.", result.Detail);
    }

    [Fact]
    public async Task A_per_user_app_without_a_requester_is_refused()
    {
        var result = await Executor(new FakeProcesses(_ => new ProcessResult(0, "")), new FakeSessions())
            .RunAsync(new JobContext("job-4", null), Definition with { Scope = "user" }, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.WaitingForUser);
    }

    [Theory]
    [InlineData(PortableAppExecutor.InUse, "Vendor Tool is open. Close it and install again.")]
    [InlineData(PortableAppExecutor.NotAnArchive, "The download is not a zip archive, or it is damaged.")]
    [InlineData(PortableAppExecutor.NoProgram, @"The archive has no bin\tool.exe. Check the program's path in the catalog.")]
    public async Task Each_way_the_script_stops_has_its_own_sentence(int exitCode, string detail)
    {
        var result = await Executor(new FakeProcesses(_ => new ProcessResult(exitCode, "")), new FakeSessions())
            .RunAsync(new JobContext("job-5", null), Definition, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(detail, result.Detail);
        Assert.Equal(exitCode, result.ExitCode);
    }

    [Fact]
    public async Task Any_other_failure_carries_what_powershell_said()
    {
        var result = await Executor(new FakeProcesses(_ => new ProcessResult(1, "Access to the path is denied.")), new FakeSessions())
            .RunAsync(new JobContext("job-6", null), Definition, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Unpacking failed with exit code 1. Access to the path is denied.", result.Detail);
    }

    [Fact]
    public async Task A_mismatched_download_is_never_unpacked()
    {
        var processes = new FakeProcesses(_ => new ProcessResult(0, ""));
        var wrong = Definition with { Sha256 = new string('f', 64) };

        var result = await Executor(processes, new FakeSessions(), serve: true)
            .RunAsync(new JobContext("job-7", null), wrong, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("Checksum mismatch", result.Detail);
        Assert.Empty(processes.Started);
    }

    [Fact]
    public async Task Removal_runs_a_fresh_script_and_reports_an_open_folder()
    {
        var processes = new FakeProcesses(_ => new ProcessResult(PortableAppExecutor.InUse, ""));

        var result = await Executor(processes, new FakeSessions()).UninstallAsync(
            new JobContext("job-8", null, InstallKind.Uninstall), Definition, new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Vendor Tool is open. Close it and remove it again.", result.Detail);
        Assert.Contains("job-8.remove.ps1", Assert.Single(processes.Started).Arguments);
    }

    [Fact]
    public async Task A_removal_that_works_says_so()
    {
        var result = await Executor(new FakeProcesses(_ => new ProcessResult(0, "")), new FakeSessions()).UninstallAsync(
            new JobContext("job-9", null, InstallKind.Uninstall), Definition, new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Removed.", result.Detail);
    }

    [Fact]
    public void The_real_script_installs_for_the_current_account_and_removes_everything_again()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = "AppPortalTest " + Guid.NewGuid().ToString("N")[..8];
        var definition = Definition with { Scope = "user", Folder = folder, ShortcutName = folder };
        var zip = Zip(("Tool/bin/tool.exe", "not a real program"), ("Tool/readme.txt", "hello"));
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "App Portal Apps", folder);
        var link = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), folder + ".lnk");
        var key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppPortalPortable-" + folder;
        try
        {
            Assert.Equal(0, RunScript(PortableAppExecutor.InstallScript(definition with { Executable = @"Tool\bin\tool.exe" }, zip)));

            Assert.True(File.Exists(Path.Combine(target, "Tool", "bin", "tool.exe")));
            Assert.True(File.Exists(Path.Combine(target, "uninstall-app-portal.ps1")));
            Assert.True(File.Exists(link));
            using (var entry = Registry.CurrentUser.OpenSubKey(key))
            {
                Assert.NotNull(entry);
                Assert.Equal(folder, entry.GetValue("DisplayName"));
                Assert.Equal("2.1", entry.GetValue("DisplayVersion"));
                Assert.Equal(target, entry.GetValue("InstallLocation"));
                Assert.Contains("uninstall-app-portal.ps1", (string)entry.GetValue("QuietUninstallString")!);
            }

            // Installing again over itself replaces the folder: that is what an update is.
            Assert.Equal(0, RunScript(PortableAppExecutor.InstallScript(definition with { Executable = @"Tool\bin\tool.exe" }, zip)));
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(target)!, "." + folder + ".old")));

            // The removal Settings would run, the copy left in the folder, takes all three away.
            Assert.Equal(0, RunScriptFile(Path.Combine(target, "uninstall-app-portal.ps1")));
            Assert.False(Directory.Exists(target));
            Assert.False(File.Exists(link));
            Assert.Null(Registry.CurrentUser.OpenSubKey(key));

            // And removing what is already gone is not an error.
            Assert.Equal(0, RunScript(PortableAppExecutor.RemovalScript(definition)));
        }
        finally
        {
            Clean(target, link, key);
        }
    }

    [Fact]
    public void A_file_that_is_open_stops_the_install_and_leaves_the_old_copy_whole()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = "AppPortalTest " + Guid.NewGuid().ToString("N")[..8];
        var definition = Definition with { Scope = "user", Folder = folder, ShortcutName = null, Executable = "tool.exe" };
        var zip = Zip(("tool.exe", "first"), ("data.txt", "first"));
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "App Portal Apps", folder);
        var key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppPortalPortable-" + folder;
        try
        {
            Assert.Equal(0, RunScript(PortableAppExecutor.InstallScript(definition, zip)));
            using (new FileStream(Path.Combine(target, "data.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Equal(PortableAppExecutor.InUse, RunScript(PortableAppExecutor.InstallScript(definition, Zip(("tool.exe", "second")))));
            }

            Assert.Equal("first", File.ReadAllText(Path.Combine(target, "data.txt")));
            Assert.Equal("first", File.ReadAllText(Path.Combine(target, "tool.exe")));
        }
        finally
        {
            Clean(target, null, key);
        }
    }

    [Fact]
    public void An_archive_that_reaches_outside_its_folder_is_refused()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = "AppPortalTest " + Guid.NewGuid().ToString("N")[..8];
        var definition = Definition with { Scope = "user", Folder = folder, ShortcutName = null, Executable = "tool.exe" };
        var zip = Zip(("tool.exe", "x"), ("../../escaped-" + folder + ".txt", "x"));
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "App Portal Apps");
        var key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppPortalPortable-" + folder;
        try
        {
            Assert.Equal(PortableAppExecutor.NotAnArchive, RunScript(PortableAppExecutor.InstallScript(definition, zip)));
            Assert.False(Directory.Exists(Path.Combine(apps, folder)));
            Assert.False(File.Exists(Path.Combine(apps, "..", "..", "escaped-" + folder + ".txt")));
            Assert.False(File.Exists(Path.Combine(apps, "..", "escaped-" + folder + ".txt")));
        }
        finally
        {
            Clean(Path.Combine(apps, folder), null, key);
        }
    }

    [Fact]
    public void A_zip_without_the_program_is_refused_and_leaves_nothing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var folder = "AppPortalTest " + Guid.NewGuid().ToString("N")[..8];
        var definition = Definition with { Scope = "user", Folder = folder, ShortcutName = null, Executable = "missing.exe" };
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "App Portal Apps");

        Assert.Equal(PortableAppExecutor.NoProgram, RunScript(PortableAppExecutor.InstallScript(definition, Zip(("other.exe", "x")))));
        Assert.False(Directory.Exists(Path.Combine(apps, folder)));
        Assert.False(Directory.Exists(Path.Combine(apps, "." + folder + ".new")));

        var notZip = Path.Combine(_root, "not-a.zip");
        File.WriteAllText(notZip, "plain text");
        Assert.Equal(PortableAppExecutor.NotAnArchive, RunScript(PortableAppExecutor.InstallScript(definition, notZip)));
    }

    private string Zip(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }

        return path;
    }

    private int RunScript(string script)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(path, script);
        return RunScriptFile(path);
    }

    private static int RunScriptFile(string path)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(120_000);
        return process.ExitCode;
    }

    private static void Clean(string target, string? link, string key)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parent = Path.GetDirectoryName(target)!;
        var name = Path.GetFileName(target);
        foreach (var directory in new[] { target, Path.Combine(parent, "." + name + ".new"), Path.Combine(parent, "." + name + ".old"),
                     Path.Combine(parent, "." + name + ".removed") })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
        {
            // The folder the first portable app on this account made. A test leaves the account as it found it.
            Directory.Delete(parent);
        }

        if (link is not null && File.Exists(link))
        {
            File.Delete(link);
        }

        Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
    }

    private PortableAppExecutor Executor(IProcessRunner processes, IUserSessionLauncher sessions, bool serve = false)
    {
        var downloads = Path.Combine(_root, "downloads");
        Directory.CreateDirectory(downloads);
        var cache = new InstallerCache(downloads, NullLogger.Instance);
        HttpClient http;
        if (serve)
        {
            http = new HttpClient(new Serve(_body));
        }
        else
        {
            // Already in the cache under its own hash, so the download path stays out of these tests.
            File.WriteAllBytes(cache.PathFor(Definition.Sha256, "zip"), _body);
            http = new HttpClient();
        }

        return new PortableAppExecutor(new ResumableDownload(http, cache, NullLogger.Instance),
            processes, NullLogger<PortableAppExecutor>.Instance, _root, sessions);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Progress : IProgress<(int percent, string detail)>
    {
        public void Report((int percent, string detail) value)
        {
        }
    }

    private sealed class FakeProcesses(Func<string, ProcessResult> run) : IProcessRunner
    {
        public List<(string File, string Arguments)> Started { get; } = [];

        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            Started.Add((file, arguments));
            return Task.FromResult(run(arguments));
        }
    }

    /// <summary>Answers every request with the same body.</summary>
    private sealed class Serve(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
