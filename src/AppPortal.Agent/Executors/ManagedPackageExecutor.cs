using AppPortal.Agent.Jobs;
using AppPortal.Agent.Sessions;
using AppPortal.Shared;

using Microsoft.Extensions.Logging;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Installs and removes packages through whichever of the PC's own package managers the catalog names.
/// One executor for all of them: the only thing that differs between Chocolatey and Cargo is a command
/// line, and every command line comes from the manager's row in <see cref="PackageManagers"/>. The
/// row is data, so the eleventh manager needs no code here.
/// </summary>
public sealed class ManagedPackageExecutor(
    IProcessRunner processes,
    IUserSessionLauncher sessions,
    IPackageManagerLocator locator,
    ILogger<ManagedPackageExecutor> logger,
    string stateDirectory,
    TimeSpan? timeout = null) : IPackageExecutor
{
    private const int RebootRequired = 3010;
    private const int RebootInitiated = 1641;

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(60);

    public string Kind => "managed";

    public Task<ExecutionResult> RunAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
        => ExecuteAsync(job, definition, progress, install: true, ct);

    public Task<ExecutionResult> UninstallAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
        => ExecuteAsync(job, definition, progress, install: false, ct);

    private async Task<ExecutionResult> ExecuteAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, bool install, CancellationToken ct)
    {
        if (definition is not ManagedPackageDefinition package)
        {
            return new ExecutionResult(false, "This job is not a package manager package.");
        }

        var manager = PackageManagers.Find(package.Manager);
        if (manager is null)
        {
            // The same shape as the message for an unknown kind, and for the same reason: a newer
            // server can describe what this agent cannot yet do, and that is a fact to report once,
            // not a failure to retry three times.
            return new ExecutionResult(false,
                $"This agent does not know the package manager {package.Manager}. It is likely older than the server.");
        }

        var perUser = package.Scope == "user";
        if (perUser && string.IsNullOrWhiteSpace(job.Requester))
        {
            return new ExecutionResult(false, "This package installs for one person, and the job does not say who.");
        }

        var executable = locator.Find(manager, perUser ? job.Requester : null);
        if (executable is null)
        {
            var whose = perUser ? $" for {job.Requester}" : "";
            return new ExecutionResult(false,
                $"{manager.DisplayName} is not installed on this PC{whose}. An administrator can add it to the catalog as a prerequisite of this app.");
        }

        var arguments = install
            ? manager.InstallArguments(package.Id, package.Version, package.Scope, package.ExtraArgs)
            : manager.UninstallArguments(package.Id, package.Scope);
        var log = new JobLog(stateDirectory, job.JobId);
        log.Write($"{executable} {arguments}");
        progress.Report((0, install ? "Installing" : "Removing"));

        ProcessResult? result;
        try
        {
            if (perUser)
            {
                result = await sessions.RunAsAsync(job.Requester!, executable, arguments, log.Write, _timeout, ct);
                if (result is null)
                {
                    log.Write($"waiting for {job.Requester} to sign in");
                    return new ExecutionResult(false, $"Waiting for {job.Requester} to sign in.", null, WaitingForUser: true);
                }
            }
            else
            {
                result = await processes.RunAsync(executable, arguments, line => Report(line, log, progress), _timeout, ct);
            }
        }
        catch (TimeoutException ex)
        {
            log.Write(ex.Message);
            return new ExecutionResult(false, ex.Message);
        }

        log.Write($"exit {result.ExitCode}");
        logger.LogInformation("{Manager} {Verb} {Package} exited {ExitCode}",
            manager.Name, install ? "install" : "uninstall", package.Id, result.ExitCode);
        return Interpret(result, manager, package, install);
    }

    private static ExecutionResult Interpret(ProcessResult result, PackageManagerDescriptor manager,
        ManagedPackageDefinition package, bool install)
    {
        if (result.ExitCode == 0)
        {
            return new ExecutionResult(true, install ? "Installed." : "Removed.", 0);
        }

        if (manager.AlreadyInstalled.Contains(result.ExitCode))
        {
            // The state the caller wanted, so not a failure, on either verb: a removal of something
            // that is not there has nothing left to do either.
            return new ExecutionResult(true, install ? "Already installed." : "It was not installed.", result.ExitCode);
        }

        if (result.ExitCode is RebootRequired or RebootInitiated)
        {
            return DirectInstallerExecutor.Restart(result.ExitCode) with
            {
                Detail = install ? "Installed. This PC has to restart to finish." : "Removed. This PC has to restart to finish.",
            };
        }

        var tail = WingetOutput.Tail(result.Output, 400).ReplaceLineEndings(" ").Trim();
        var verb = install ? "install" : "remove";
        return new ExecutionResult(false,
            $"{manager.DisplayName} could not {verb} {package.Id} (exit code {result.ExitCode})." + (tail.Length > 0 ? " " + tail : ""),
            result.ExitCode);
    }

    /// <summary>
    /// Every one of these managers redraws a progress line and mentions the phase it is in, which is
    /// the same shape winget's output has, so the same reader serves. The word Downloading in a detail
    /// is what the job runner turns into the downloading state, and it is reported only while the
    /// manager itself says it is downloading.
    /// </summary>
    private static void Report(string line, JobLog log, IProgress<(int percent, string detail)> progress)
    {
        log.Write(line);
        var (percent, detail) = WingetOutput.Read(line);
        if (detail is not null)
        {
            progress.Report((percent ?? 0, detail));
        }
    }
}
