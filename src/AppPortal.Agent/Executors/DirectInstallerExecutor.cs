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

    private ExecutionResult Interpret(ProcessResult result)
    {
        switch (result.ExitCode)
        {
            case 0:
                return new ExecutionResult(true, "Installed.", 0);
            case RebootRequired:
            case RebootInitiated:
                // M3-09 turns this into a restart the person is actually asked for. Until then the
                // detail is the only place it is said, which is why it is said plainly.
                return new ExecutionResult(true, "Installed. Restart required.", result.ExitCode);
            default:
                var tail = WingetOutput.Tail(result.Output, 400).ReplaceLineEndings(" ").Trim();
                logger.LogWarning("The installer exited {Code}", result.ExitCode);
                return new ExecutionResult(false, tail.Length == 0
                    ? $"The installer failed with exit code {result.ExitCode}."
                    : $"The installer failed with exit code {result.ExitCode}. {tail}", result.ExitCode);
        }
    }
}
