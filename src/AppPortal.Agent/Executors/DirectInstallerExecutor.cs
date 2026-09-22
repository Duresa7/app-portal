using AppPortal.Agent.Downloads;
using AppPortal.Agent.Jobs;
using AppPortal.Agent.Sessions;
using AppPortal.Shared;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Downloads an installer, proves it is the one the catalog described, and runs it silently as SYSTEM.
/// This is the path for software no package source carries: an internal line-of-business MSI, a vendor
/// who ships only a download link, and the large installers that make resuming worth the trouble.
/// </summary>
public sealed class DirectInstallerExecutor(
    ResumableDownload downloads,
    IProcessRunner processes,
    ILogger<DirectInstallerExecutor> logger,
    string stateDirectory,
    IUserSessionLauncher sessions,
    IUninstallRegistry registry,
    TimeSpan? timeout = null) : IPackageExecutor
{
    private const int RebootRequired = 3010;

    private const int RebootInitiated = 1641;

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(60);

    public string Kind => "direct";

    public async Task<ExecutionResult> RunAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        if (definition is not DirectPackageDefinition direct)
        {
            return new ExecutionResult(false, "This job is not a direct installer.");
        }

        if (direct.Scope == "user" && string.IsNullOrWhiteSpace(job.Requester))
        {
            return new ExecutionResult(false, "This package installs for one person, and the install does not say who asked.");
        }

        var log = new JobLog(stateDirectory, job.JobId);
        string installer;
        try
        {
            installer = await downloads.FetchAsync(direct, progress, ct);
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

        var (file, arguments) = Command(direct, installer, stateDirectory, job.JobId);
        log.Write($"{file} {arguments}");
        ProcessResult? result;
        try
        {
            if (direct.Scope == "user")
            {
                // The download stayed with the service, which is right: it is the same file for
                // everyone, it is verified once, and %ProgramData%\AppPortal is readable by users, so
                // the session can run what SYSTEM fetched without a second copy per person.
                progress.Report((100, $"Installing for {job.Requester}"));
                result = await sessions.RunAsAsync(job.Requester!, file, arguments, log.Write, _timeout, ct);
                if (result is null)
                {
                    log.Write($"waiting for {job.Requester} to sign in");
                    return new ExecutionResult(false, $"Waiting for {job.Requester} to sign in.", null, WaitingForUser: true);
                }
            }
            else
            {
                progress.Report((100, "Installing"));
                result = await processes.RunAsync(file, arguments, log.Write, _timeout, ct);
            }
        }
        catch (TimeoutException ex)
        {
            log.Write(ex.Message);
            return new ExecutionResult(false, ex.Message);
        }

        log.Write($"exit {result.ExitCode}");
        return Interpret(result);
    }

    public async Task<ExecutionResult> UninstallAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        if (definition is not DirectPackageDefinition direct)
        {
            return new ExecutionResult(false, "This job is not a direct installer.");
        }

        var log = new JobLog(stateDirectory, job.JobId);
        // Whose copy this is has to be settled before the lookup, not after it. A per-user application
        // writes its uninstall entry into that person's hive and nowhere else, so a lookup that does
        // not know the account reads the machine, finds nothing, and reports that the application
        // offers no silent removal when it plainly does.
        if (direct.Scope == "user" && string.IsNullOrWhiteSpace(job.Requester))
        {
            return new ExecutionResult(false, "This package belongs to one person, and the removal does not say who.");
        }

        var command = UninstallCommand(direct, registry, direct.Scope == "user" ? job.Requester : null);
        if (command is null)
        {
            // An exe whose uninstall entry offers only an interactive command is a dead end from a
            // service: running it would open a window on somebody's screen and wait for them.
            return new ExecutionResult(false,
                "This app does not offer a silent way to remove it. Remove it from Settings on the PC.");
        }

        progress.Report((0, "Removing"));
        log.Write($"{command.Value.File} {command.Value.Arguments}");
        ProcessResult? result;
        if (direct.Scope == "user")
        {
            result = await sessions.RunAsAsync(job.Requester!, command.Value.File, command.Value.Arguments, log.Write, _timeout, ct);
            if (result is null)
            {
                return new ExecutionResult(false, $"Waiting for {job.Requester} to sign in.", null, WaitingForUser: true);
            }
        }
        else
        {
            result = await processes.RunAsync(command.Value.File, command.Value.Arguments, log.Write, _timeout, ct);
        }

        log.Write($"exit {result.ExitCode}");
        return result.ExitCode switch
        {
            0 => new ExecutionResult(true, "Removed.", 0),
            RebootRequired or RebootInitiated => Restart(result.ExitCode) with { Detail = "Removed. This PC has to restart to finish." },
            _ => new ExecutionResult(false, $"The app could not be removed (exit code {result.ExitCode}).", result.ExitCode),
        };
    }

    /// <summary>
    /// How to take this package off, or null when there is no way to do it without somebody watching.
    /// An msix is removed by name; an msi by its product code; an exe by whatever quiet command its
    /// own uninstall entry documents, which not every vendor bothers to write.
    /// </summary>
    internal static (string File, string Arguments)? UninstallCommand(DirectPackageDefinition direct,
        IUninstallRegistry registry, string? account = null)
    {
        if (direct.InstallerType == "msix")
        {
            if (string.IsNullOrWhiteSpace(direct.UninstallKey))
            {
                return null;
            }

            var remove = direct.Scope == "user" ? "Remove-AppxPackage" : "Remove-AppxProvisionedPackage -Online -AllUsers";
            return ("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "
                                      + $"\"{remove} -PackageName '{direct.UninstallKey}'\"");
        }

        if (string.IsNullOrWhiteSpace(direct.UninstallKey))
        {
            return null;
        }

        if (direct.InstallerType == "msi")
        {
            return ("msiexec.exe", $"/x {direct.UninstallKey} /qn /norestart");
        }

        var quiet = registry.QuietUninstallString(direct.UninstallKey, account);
        if (string.IsNullOrWhiteSpace(quiet))
        {
            return null;
        }

        return Split(quiet);
    }

    /// <summary>Splits a command line into the file and the rest, honouring a quoted path.</summary>
    private static (string File, string Arguments) Split(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? (text.Trim('"'), "") : (text[1..end], text[(end + 1)..].Trim());
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    /// <summary>
    /// What to start, per installer type. An msi goes through Windows Installer with a verbose log
    /// beside the job's own; an exe gets the arguments the vendor documented; an msix has no command
    /// line at all and is handed to the packaging API through PowerShell.
    /// </summary>
    internal static (string File, string Arguments) Command(DirectPackageDefinition direct, string installer, string stateDirectory, string jobId)
        => direct.InstallerType switch
        {
            "msi" => ("msiexec.exe",
                $"/i \"{installer}\" /qn /norestart /l*v \"{Path.Combine(stateDirectory, "jobs", jobId + ".msi.log")}\""
                + (string.IsNullOrWhiteSpace(direct.SilentArgs) ? "" : " " + direct.SilentArgs.Trim())),
            // Provisioning adds the package for profiles created later, which is the machine-wide
            // form. Installing it for somebody who already has a profile is a different command, run
            // inside their session, and provisioning would not reach them.
            "msix" when direct.Scope == "user" => ("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "
                + $"\"Add-AppxPackage -Path '{installer}'\""),
            "msix" => ("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "
                + $"\"Add-AppxProvisionedPackage -Online -PackagePath '{installer}' -SkipLicense\""),
            _ => (installer, direct.SilentArgs?.Trim() ?? ""),
        };

    /// <summary>
    /// Installed, and not finished. The software is on the device and will not work until the PC
    /// restarts, so the install stays open and the person is asked rather than told afterwards.
    /// </summary>
    internal static ExecutionResult Restart(int exitCode)
        => new(true, "Installed. This PC has to restart to finish.", exitCode, NeedsRestart: true);

    private ExecutionResult Interpret(ProcessResult result)
    {
        switch (result.ExitCode)
        {
            case 0:
                return new ExecutionResult(true, "Installed.", 0);
            case RebootRequired:
            case RebootInitiated:
                return Restart(result.ExitCode);
            default:
                var tail = WingetOutput.Tail(result.Output, 400).ReplaceLineEndings(" ").Trim();
                logger.LogWarning("The installer exited {Code}", result.ExitCode);
                return new ExecutionResult(false, tail.Length == 0
                    ? $"The installer failed with exit code {result.ExitCode}."
                    : $"The installer failed with exit code {result.ExitCode}. {tail}", result.ExitCode);
        }
    }
}
