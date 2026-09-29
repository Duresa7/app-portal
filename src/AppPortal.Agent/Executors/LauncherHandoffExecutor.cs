using AppPortal.Agent.Jobs;
using AppPortal.Agent.Sessions;
using AppPortal.Shared;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Hands a game to its launcher. The agent opens the game's page in the launcher on the person's own
/// desktop, and the person finishes there with their own account; the agent downloads nothing and
/// signs in to nothing. The job is done when the page is open, because from then on the launcher and
/// the person decide, and the game reaches the Installed list the way any software does, from its
/// launcher's uninstall entry at the next sweep.
/// </summary>
public sealed class LauncherHandoffExecutor(
    IUserSessionLauncher sessions,
    IProtocolRegistry protocols,
    ILogger<LauncherHandoffExecutor> logger,
    string stateDirectory,
    TimeSpan? timeout = null) : IPackageExecutor
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(2);

    public string Kind => "launcher";

    public Task<ExecutionResult> RunAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
        => HandOffAsync(job, definition, launcher => launcher.InstallUri, "Finish the install there.", progress, ct);

    public Task<ExecutionResult> UninstallAsync(JobContext job, PackageDefinition definition,
        IProgress<(int percent, string detail)> progress, CancellationToken ct)
        => HandOffAsync(job, definition, launcher => launcher.UninstallUri, "Finish the removal there.", progress, ct);

    private async Task<ExecutionResult> HandOffAsync(JobContext job, PackageDefinition definition,
        Func<GameLauncher, string?> template, string finish, IProgress<(int percent, string detail)> progress, CancellationToken ct)
    {
        if (definition is not LauncherPackageDefinition game)
        {
            return new ExecutionResult(false, "This job is not a game for a launcher.");
        }

        if (game.Row is not { } launcher)
        {
            return new ExecutionResult(false, $"This agent does not know the launcher '{game.Launcher}'. It is likely older than the server.");
        }

        if (string.IsNullOrWhiteSpace(job.Requester))
        {
            return new ExecutionResult(false, "A game is handed to its launcher for one person, and this job does not say who asked.");
        }

        if (template(launcher) is not { } link)
        {
            return new ExecutionResult(false, $"Remove it in {launcher.DisplayName}.");
        }

        // Signed in first: their registrations live in their hive, which is only loaded while they are,
        // so a person who is away would otherwise read as somebody without the launcher.
        if (!sessions.SignedInAccounts().Contains(job.Requester, StringComparer.OrdinalIgnoreCase))
        {
            return new ExecutionResult(false, $"Waiting for {job.Requester} to sign in.", null, WaitingForUser: true);
        }

        if (!protocols.IsRegistered(launcher.Scheme, job.Requester))
        {
            return new ExecutionResult(false,
                $"{launcher.DisplayName} is not installed for {job.Requester}. Install {launcher.DisplayName} first.");
        }

        var uri = launcher.Uri(link, game.GameId);
        var log = new JobLog(stateDirectory, job.JobId);
        log.Write($"opening {uri} for {job.Requester}");
        progress.Report((100, $"Opening {launcher.DisplayName}"));

        // rundll32 hands the link to whatever registered its scheme and exits 0, where explorer.exe
        // does the same and exits 1, which would read as a failure every time.
        var result = await sessions.RunAsAsync(job.Requester, "rundll32.exe", $"url.dll,FileProtocolHandler {uri}", log.Write,
            _timeout, ct);
        if (result is null)
        {
            return new ExecutionResult(false, $"Waiting for {job.Requester} to sign in.", null, WaitingForUser: true);
        }

        log.Write($"exit {result.ExitCode}");
        if (result.ExitCode != 0)
        {
            logger.LogWarning("Opening {Launcher} exited {Code}", launcher.DisplayName, result.ExitCode);
            return new ExecutionResult(false, $"Windows could not open {launcher.DisplayName} (exit code {result.ExitCode}).", result.ExitCode);
        }

        return new ExecutionResult(true, $"Opened in {launcher.DisplayName}. {finish}", 0);
    }
}
