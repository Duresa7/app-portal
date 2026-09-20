using AppPortal.Agent.Jobs;
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

    public async Task<ExecutionResult> RunAsync(string jobId, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        if (definition is not WingetPackageDefinition winget)
        {
            return new ExecutionResult(false, "This job is not a winget package.");
        }

        if (winget.Scope == "user")
        {
            // M3-07 runs an installer inside the session of the person who asked. Until it does, saying
            // so plainly beats installing into the service account's profile and reporting success.
            return new ExecutionResult(false, "This package installs for one person, and the agent cannot yet run in a user session.");
        }

        var executable = _locator.Find();
        if (executable is null)
        {
            return new ExecutionResult(false, "winget is not installed on this PC. Install the App Installer from the Microsoft Store.");
        }

        var log = new JobLog(stateDirectory, jobId);
        await UpdateSourcesAsync(executable, log, ct);

        progress.Report((0, "Installing"));
        var arguments = Arguments(winget);
        log.Write($"winget {arguments}");
        ProcessResult result;
        try
        {
            result = await processes.RunAsync(executable, arguments, line => Report(line, log, progress), _timeout, ct);
        }
        catch (TimeoutException ex)
        {
            log.Write(ex.Message);
            return new ExecutionResult(false, ex.Message);
        }

        log.Write($"exit {result.ExitCode}");
        return Interpret(result, winget);
    }

    internal static string Arguments(WingetPackageDefinition winget)
    {
        var arguments = new List<string>
        {
            "install",
            "--id", winget.Id,
            "--exact",
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
        RebootRequired or RebootInitiated => new ExecutionResult(true, "Installed. Restart required.", result.ExitCode),
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
    private async Task UpdateSourcesAsync(string executable, JobLog log, CancellationToken ct)
    {
        var marker = Path.Combine(stateDirectory, "winget-source-updated");
        try
        {
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < TimeSpan.FromDays(1))
            {
                return;
            }

            var result = await processes.RunAsync(executable, "source update --disable-interactivity", null, TimeSpan.FromMinutes(5), ct);
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
