using AppPortal.Agent.Jobs;
using AppPortal.Agent.Sessions;
using AppPortal.Shared;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Installs a winget package as SYSTEM. Everything that makes this awkward is Windows rather than
/// winget: the executable is not on the service account's PATH, the output is a redrawn progress bar,
/// and several of the exit codes that mean "nothing to do" are not zero.
/// </summary>
public sealed class WingetExecutor(
    IProcessRunner processes,
    ILogger<WingetExecutor> logger,
    string stateDirectory,
    IUserSessionLauncher sessions,
    WingetLocator? locator = null,
    TimeSpan? timeout = null) : IPackageExecutor
{
    /// <summary>The package is already there at the version asked for. Nothing to do is not a failure.</summary>
    private const int NoApplicableUpdate = unchecked((int)0x8A150011);

    /// <summary>No installer in the manifest matches this device, which is what a per-user package answers to a machine-scope install.</summary>
    private const int NoApplicableInstaller = unchecked((int)0x8A15002B);

    private const int RebootRequired = 3010;

    private const int RebootInitiated = 1641;

    private readonly WingetLocator _locator = locator ?? new WingetLocator();

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(60);

    public string Kind => "winget";

    public async Task<ExecutionResult> RunAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        if (definition is not WingetPackageDefinition winget)
        {
            return new ExecutionResult(false, "This job is not a winget package.");
        }

        if (winget.Scope == "user" && string.IsNullOrWhiteSpace(job.Requester))
        {
            // Without an account there is no profile to install into, and guessing at one would put
            // somebody else's software on their desktop.
            return new ExecutionResult(false, "This package installs for one person, and the install does not say who asked.");
        }

        var executable = _locator.Find();
        if (executable is null)
        {
            return new ExecutionResult(false, "winget is not installed on this PC. Install the App Installer from the Microsoft Store.");
        }

        var dependencies = _locator.Dependencies(executable);
        var log = new JobLog(stateDirectory, job.JobId);
        await UpdateSourcesAsync(executable, dependencies, log, ct);

        var arguments = Arguments(winget);
        log.Write($"winget {arguments}");
        ProcessResult? result;
        try
        {
            if (winget.Scope == "user")
            {
                progress.Report((0, $"Installing for {job.Requester}"));
                // Their own alias where they have one: the package path is refused inside a session.
                var (theirs, pathFirst) = _locator.ForSession(job.Requester!, executable);
                result = await sessions.RunAsAsync(job.Requester!, theirs, arguments, pathFirst, log.Write, _timeout, ct);
                if (result is null)
                {
                    // Not a failure. The person who asked is simply not at the PC yet, and the server
                    // parks the job until they are.
                    log.Write($"waiting for {job.Requester} to sign in");
                    return new ExecutionResult(false, $"Waiting for {job.Requester} to sign in.", null, WaitingForUser: true);
                }
            }
            else
            {
                progress.Report((0, "Installing"));
                result = await processes.RunAsync(executable, arguments, dependencies, line => Report(line, log, progress), _timeout, ct);
            }
        }
        catch (TimeoutException ex)
        {
            log.Write(ex.Message);
            return new ExecutionResult(false, ex.Message);
        }

        log.Write($"exit {result.ExitCode}");
        return Interpret(result, winget);
    }

    public async Task<ExecutionResult> UninstallAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        if (definition is not WingetPackageDefinition winget)
        {
            return new ExecutionResult(false, "This job is not a winget package.");
        }

        var executable = _locator.Find();
        if (executable is null)
        {
            return new ExecutionResult(false, "winget is not installed on this PC.");
        }

        var dependencies = _locator.Dependencies(executable);
        var log = new JobLog(stateDirectory, job.JobId);
        var arguments = $"uninstall --id {Quote(winget.Id)} --exact --source {winget.Source} "
                        + $"--scope {winget.Scope} --silent --accept-source-agreements --disable-interactivity";
        log.Write($"winget {arguments}");
        progress.Report((0, "Removing"));

        ProcessResult? result;
        if (winget.Scope == "user")
        {
            if (string.IsNullOrWhiteSpace(job.Requester))
            {
                return new ExecutionResult(false, "This package belongs to one person, and the removal does not say who.");
            }

            var (theirs, pathFirst) = _locator.ForSession(job.Requester, executable);
            result = await sessions.RunAsAsync(job.Requester, theirs, arguments, pathFirst, log.Write, _timeout, ct);
            if (result is null)
            {
                return new ExecutionResult(false, $"Waiting for {job.Requester} to sign in.", null, WaitingForUser: true);
            }
        }
        else
        {
            result = await processes.RunAsync(executable, arguments, dependencies, line => Report(line, log, progress), _timeout, ct);
        }

        log.Write($"exit {result.ExitCode}");
        return result.ExitCode switch
        {
            0 => new ExecutionResult(true, "Removed.", 0),
            // Not there is the state the caller wanted, so it is not a failure.
            NoApplicableUpdate or NotInstalled => new ExecutionResult(true, "It was not installed.", result.ExitCode),
            RebootRequired or RebootInitiated => DirectInstallerExecutor.Restart(result.ExitCode) with { Detail = "Removed. This PC has to restart to finish." },
            _ => new ExecutionResult(false, $"winget could not remove {winget.Id} (exit code {result.ExitCode}).", result.ExitCode),
        };
    }

    /// <summary>Nothing matched the id, which for a removal means there was nothing to remove.</summary>
    private const int NotInstalled = unchecked((int)0x8A150014);

    internal static string Arguments(WingetPackageDefinition winget)
    {
        var arguments = new List<string>
        {
            "install",
            "--id", winget.Id,
            "--exact",
            // Always named, never left to whichever source answers first. A Store product id and a
            // winget id cannot be told apart by winget, and resolving one against the other source
            // fails with "no installer matches this PC", which reads like a packaging problem.
            "--source", winget.Source,
            "--scope", winget.Scope,
            "--silent",
            "--accept-package-agreements",
            "--accept-source-agreements",
            "--disable-interactivity",
        };

        if (!string.IsNullOrWhiteSpace(winget.Version))
        {
            arguments.Add("--version");
            arguments.Add(winget.Version);
        }

        var line = string.Join(' ', arguments.Select(Quote));
        return string.IsNullOrWhiteSpace(winget.ExtraArgs) ? line : line + " " + winget.ExtraArgs.Trim();
    }

    private static string Quote(string argument)
        => argument.Contains(' ') ? $"\"{argument}\"" : argument;

    private static ExecutionResult Interpret(ProcessResult result, WingetPackageDefinition winget) => result.ExitCode switch
    {
        0 => new ExecutionResult(true, "Installed.", 0),
        NoApplicableUpdate => new ExecutionResult(true, "Already installed.", result.ExitCode),
        RebootRequired or RebootInitiated => DirectInstallerExecutor.Restart(result.ExitCode),
        NoApplicableInstaller => new ExecutionResult(false,
            $"No installer for {winget.Id} matches this PC at {winget.Scope} scope.", result.ExitCode),
        _ => new ExecutionResult(false, Detail(result), result.ExitCode),
    };

    private static string Detail(ProcessResult result)
    {
        var tail = WingetOutput.Tail(result.Output, 400);
        return tail.Length == 0
            ? $"winget failed with exit code {result.ExitCode}."
            : $"winget failed with exit code {result.ExitCode}. {tail.ReplaceLineEndings(" ").Trim()}";
    }

    private static void Report(string line, JobLog log, IProgress<(int percent, string detail)> progress)
    {
        log.Write(line);
        var (percent, detail) = WingetOutput.Read(line);
        if (detail is not null)
        {
            progress.Report((percent ?? 0, detail));
        }
    }

    /// <summary>
    /// Refreshes the package index, at most once a day. A stale index is how an install fails with a
    /// manifest that no longer resolves, and refreshing it before every install adds a slow network
    /// call to work that is already slow.
    /// </summary>
    private async Task UpdateSourcesAsync(string executable, IReadOnlyList<string> dependencies, JobLog log, CancellationToken ct)
    {
        var marker = Path.Combine(stateDirectory, "winget-source-updated");
        try
        {
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(1))
            {
                return;
            }

            var result = await processes.RunAsync(executable, "source update --disable-interactivity", dependencies, null,
                TimeSpan.FromMinutes(5), ct);
            log.Write($"source update exit {result.ExitCode}");
            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An index that will not refresh is not a reason to refuse the install: the copy already on
            // the device is usually fine, and the install itself reports the real problem if it is not.
            logger.LogWarning("Could not refresh the winget sources ({Reason})", ex.GetType().Name);
        }
    }
}
