using System.Text;

using AppPortal.Agent.Downloads;
using AppPortal.Agent.Jobs;
using AppPortal.Agent.Sessions;
using AppPortal.Shared;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Unpacks a zip into a folder of its own and makes Windows treat it as installed: a Start menu shortcut
/// and an uninstall entry. The entry is what matters most. winget lists every uninstall entry, so the
/// inventory sweep finds a portable app like any other, and Settings can take it off again.
/// </summary>
/// <remarks>
/// The work is a PowerShell script rather than code in this service, because a per-user app has to be
/// unpacked inside the person's session: %LOCALAPPDATA% is theirs to expand, and a folder SYSTEM made in
/// their profile would be SYSTEM's. One script for both scopes keeps the two from drifting apart.
/// </remarks>
public sealed class PortableAppExecutor(
    ResumableDownload downloads,
    IProcessRunner processes,
    ILogger<PortableAppExecutor> logger,
    string stateDirectory,
    IUserSessionLauncher sessions,
    TimeSpan? timeout = null) : IPackageExecutor
{
    /// <summary>The old folder could not be moved aside, because something in it is open.</summary>
    internal const int InUse = 20;

    /// <summary>The download is not a zip, or it is damaged.</summary>
    internal const int NotAnArchive = 21;

    /// <summary>The archive holds no file at the program's path.</summary>
    internal const int NoProgram = 22;

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(30);

    public string Kind => "portable";

    public async Task<ExecutionResult> RunAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        if (definition is not PortablePackageDefinition portable)
        {
            return new ExecutionResult(false, "This job is not a portable app.");
        }

        if (portable.Scope == "user" && string.IsNullOrWhiteSpace(job.Requester))
        {
            return new ExecutionResult(false, "This app installs for one person, and the install does not say who asked.");
        }

        var log = new JobLog(stateDirectory, job.JobId);
        string archive;
        try
        {
            archive = await downloads.FetchAsync(DownloadRequest.For(portable), progress, ct);
        }
        catch (DownloadFailedException ex)
        {
            log.Write(ex.Message);
            return new ExecutionResult(false, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // The partial file stays where it is, so the next attempt asks for the rest of it.
            var message = $"The download stopped ({ex.GetType().Name}). It will resume on the next attempt.";
            log.Write(message);
            return new ExecutionResult(false, message);
        }

        progress.Report((100, portable.Scope == "user" ? $"Unpacking for {job.Requester}" : "Unpacking"));
        var result = await RunScriptAsync(job, portable, "portable", InstallScript(portable, archive), log, ct);
        if (result is null)
        {
            log.Write($"waiting for {job.Requester} to sign in");
            return new ExecutionResult(false, $"Waiting for {job.Requester} to sign in.", null, WaitingForUser: true);
        }

        log.Write($"exit {result.ExitCode}");
        return result.ExitCode switch
        {
            0 => new ExecutionResult(true, "Installed.", 0),
            InUse => new ExecutionResult(false, $"{portable.Folder} is open. Close it and install again.", InUse),
            NotAnArchive => new ExecutionResult(false, "The download is not a zip archive, or it is damaged.", NotAnArchive),
            NoProgram => new ExecutionResult(false,
                $"The archive has no {portable.Executable}. Check the program's path in the catalog.", NoProgram),
            _ => Failed("Unpacking", result),
        };
    }

    public async Task<ExecutionResult> UninstallAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        if (definition is not PortablePackageDefinition portable)
        {
            return new ExecutionResult(false, "This job is not a portable app.");
        }

        if (portable.Scope == "user" && string.IsNullOrWhiteSpace(job.Requester))
        {
            return new ExecutionResult(false, "This app belongs to one person, and the removal does not say who.");
        }

        var log = new JobLog(stateDirectory, job.JobId);
        progress.Report((0, "Removing"));

        // A fresh copy of the removal rather than the one left in the app's folder: that one is in a
        // folder the person can write to, and this runs as SYSTEM for a machine-wide app.
        var result = await RunScriptAsync(job, portable, "remove", RemovalScript(portable), log, ct);
        if (result is null)
        {
            return new ExecutionResult(false, $"Waiting for {job.Requester} to sign in.", null, WaitingForUser: true);
        }

        log.Write($"exit {result.ExitCode}");
        return result.ExitCode switch
        {
            0 => new ExecutionResult(true, "Removed.", 0),
            InUse => new ExecutionResult(false, $"{portable.Folder} is open. Close it and remove it again.", InUse),
            _ => Failed("Removing", result),
        };
    }

    /// <summary>Null when the app is for one person and that person is not signed in.</summary>
    private async Task<ProcessResult?> RunScriptAsync(JobContext job, PortablePackageDefinition portable, string suffix,
        string script, JobLog log, CancellationToken ct)
    {
        var path = Path.Combine(stateDirectory, "jobs", $"{JobLog.SafeName(job.JobId)}.{suffix}.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, script, Encoding.ASCII, ct);
        var arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{path}\"";
        log.Write($"powershell.exe {arguments}");
        try
        {
            return portable.Scope == "user"
                ? await sessions.RunAsAsync(job.Requester!, "powershell.exe", arguments, log.Write, _timeout, ct)
                : await processes.RunAsync("powershell.exe", arguments, log.Write, _timeout, ct);
        }
        catch (TimeoutException ex)
        {
            log.Write(ex.Message);
            return new ProcessResult(-1, ex.Message);
        }
    }

    private ExecutionResult Failed(string what, ProcessResult result)
    {
        var tail = WingetOutput.Tail(result.Output, 400).ReplaceLineEndings(" ").Trim();
        logger.LogWarning("{What} a portable app exited {Code}", what, result.ExitCode);
        return new ExecutionResult(false, tail.Length == 0
            ? $"{what} failed with exit code {result.ExitCode}."
            : $"{what} failed with exit code {result.ExitCode}. {tail}", result.ExitCode);
    }

    /// <summary>
    /// Unpacks next to the target and swaps the folders, so that a file somebody has open stops the
    /// install before anything is lost. Moving a folder fails while a file in it is open, which is the
    /// check; deleting it first would leave half an old copy behind when that happened.
    /// </summary>
    internal static string InstallScript(PortablePackageDefinition portable, string archive)
    {
        var script = new StringBuilder();
        Preamble(script, portable);
        script.AppendLine($"$zip = {Literal(archive)}");
        script.AppendLine($"$executable = {Literal(portable.Executable.Replace('/', '\\'))}");
        script.AppendLine($"$display = {Literal(portable.DisplayName)}");
        script.AppendLine($"$version = {Literal(portable.Version ?? "")}");
        script.AppendLine("""
            $staging = Join-Path $root ('.' + $folder + '.new')
            $old = Join-Path $root ('.' + $folder + '.old')
            New-Item -ItemType Directory -Force -Path $root | Out-Null
            foreach ($leftover in @($staging, $old)) {
                if (Test-Path -LiteralPath $leftover) { Remove-Item -LiteralPath $leftover -Recurse -Force }
            }
            try {
                Add-Type -AssemblyName System.IO.Compression.FileSystem
                [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $staging)
            } catch {
                if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
                [Console]::Error.WriteLine($_.Exception.Message)
                exit 21
            }
            if (-not (Test-Path -LiteralPath (Join-Path $staging $executable) -PathType Leaf)) {
                Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
                exit 22
            }
            if (Test-Path -LiteralPath $target) {
                try { Rename-Item -LiteralPath $target -NewName ('.' + $folder + '.old') }
                catch {
                    Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
                    [Console]::Error.WriteLine($_.Exception.Message)
                    exit 20
                }
            }
            Rename-Item -LiteralPath $staging -NewName $folder
            if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force -ErrorAction SilentlyContinue }
            $program = Join-Path $target $executable
            $removal = Join-Path $target 'uninstall-app-portal.ps1'
            """);
        script.AppendLine("Set-Content -LiteralPath $removal -Encoding ASCII -Value @'");
        script.Append(RemovalScript(portable));
        script.AppendLine("'@");
        script.AppendLine("""
            if ($shortcut) {
                New-Item -ItemType Directory -Force -Path $menu | Out-Null
                $shell = New-Object -ComObject WScript.Shell
                $link = $shell.CreateShortcut($linkPath)
                $link.TargetPath = $program
                $link.WorkingDirectory = Split-Path -Parent $program
                $link.Save()
            }
            $command = 'powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $removal + '"'
            New-Item -Path $key -Force | Out-Null
            New-ItemProperty -LiteralPath $key -Name DisplayName -Value $display -PropertyType String -Force | Out-Null
            if ($version) { New-ItemProperty -LiteralPath $key -Name DisplayVersion -Value $version -PropertyType String -Force | Out-Null }
            New-ItemProperty -LiteralPath $key -Name Publisher -Value 'App Portal' -PropertyType String -Force | Out-Null
            New-ItemProperty -LiteralPath $key -Name InstallLocation -Value $target -PropertyType String -Force | Out-Null
            New-ItemProperty -LiteralPath $key -Name DisplayIcon -Value $program -PropertyType String -Force | Out-Null
            New-ItemProperty -LiteralPath $key -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
            New-ItemProperty -LiteralPath $key -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null
            New-ItemProperty -LiteralPath $key -Name UninstallString -Value $command -PropertyType String -Force | Out-Null
            New-ItemProperty -LiteralPath $key -Name QuietUninstallString -Value $command -PropertyType String -Force | Out-Null
            exit 0
            """);
        return script.ToString();
    }

    /// <summary>
    /// Takes the folder, the shortcut and the entry away. The folder is moved aside before it is
    /// deleted, for the same reason the install moves it: a file that is open stops the removal whole.
    /// A folder that is already gone is removed, not an error.
    /// </summary>
    internal static string RemovalScript(PortablePackageDefinition portable)
    {
        var script = new StringBuilder();
        Preamble(script, portable);
        script.AppendLine("""
            if (Test-Path -LiteralPath $target) {
                $gone = Join-Path $root ('.' + $folder + '.removed')
                if (Test-Path -LiteralPath $gone) { Remove-Item -LiteralPath $gone -Recurse -Force -ErrorAction SilentlyContinue }
                try { Rename-Item -LiteralPath $target -NewName ('.' + $folder + '.removed') }
                catch { [Console]::Error.WriteLine($_.Exception.Message); exit 20 }
                Remove-Item -LiteralPath $gone -Recurse -Force -ErrorAction SilentlyContinue
            }
            if ($shortcut -and (Test-Path -LiteralPath $linkPath)) { Remove-Item -LiteralPath $linkPath -Force }
            if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
            exit 0
            """);
        return script.ToString();
    }

    /// <summary>
    /// Where everything goes, for the scope. The locations come from the environment of whoever runs
    /// the script, which for a per-user app is the person, and that is the whole reason it runs there.
    /// </summary>
    private static void Preamble(StringBuilder script, PortablePackageDefinition portable)
    {
        var user = portable.Scope == "user";
        script.AppendLine("# Written by the App Portal agent. Every value below passed the catalog's rules.");
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine("$ProgressPreference = 'SilentlyContinue'");
        script.AppendLine($"$folder = {Literal(portable.Folder)}");
        script.AppendLine($"$shortcut = {Literal(portable.ShortcutName ?? "")}");
        script.AppendLine(user
            ? "$root = Join-Path $env:LOCALAPPDATA 'Programs\\App Portal Apps'"
            : "$root = Join-Path $env:ProgramFiles 'App Portal Apps'");
        script.AppendLine(user
            ? "$menu = Join-Path $env:APPDATA 'Microsoft\\Windows\\Start Menu\\Programs'"
            : "$menu = Join-Path $env:ProgramData 'Microsoft\\Windows\\Start Menu\\Programs'");
        script.AppendLine(user
            ? "$key = 'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\AppPortalPortable-' + $folder"
            : "$key = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\AppPortalPortable-' + $folder");
        script.AppendLine("$target = Join-Path $root $folder");
        script.AppendLine("$linkPath = Join-Path $menu ($shortcut + '.lnk')");
    }

    /// <summary>
    /// A single-quoted PowerShell string, in which nothing is expanded and a quote is the only character
    /// with a meaning. The catalog's rules already keep quotes out of every value; the doubling is for
    /// the archive's path, which the agent chose, and for any rule that is loosened one day.
    /// </summary>
    internal static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
}
