using AppPortal.Agent.Jobs;

namespace AppPortal.Agent.Update;

/// <summary>
/// When the agent looks for a new release: once when the service starts, once a day at a random
/// second between noon and one, and within ten seconds of a client leaving update.request behind.
/// Lunchtime is when a PC is on and nobody is half way through anything, and the random second keeps
/// a site's worth of machines from asking GitHub in the same breath.
/// </summary>
public sealed class UpdateWorker(
    SelfUpdate update,
    UpdatePaths paths,
    IProcessRunner processes,
    ILogger<UpdateWorker> logger,
    TimeSpan? pollInterval = null,
    bool? windows = null) : BackgroundService
{
    /// <summary>The scheduled task the zip updater installed, which an MSI upgrade has to be rid of.</summary>
    public const string LegacyTaskName = "App Portal Updater";

    /// <summary>The SID of the local Users group, which is the same on every Windows in every language.</summary>
    private const string UsersSid = "*S-1-5-32-545";

    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(10);
    private readonly bool _windows = windows ?? OperatingSystem.IsWindows();

    /// <summary>
    /// The next check, at a random second in the noon hour. A time already gone today is tomorrow's.
    /// </summary>
    internal static DateTimeOffset NextDailyCheck(DateTimeOffset now, Random random)
    {
        var at = new DateTimeOffset(now.Year, now.Month, now.Day, 12, 0, 0, now.Offset).AddSeconds(random.Next(3600));
        return at > now ? at : at.AddDays(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_windows)
        {
            await RemoveLegacyTaskAsync(stoppingToken);
            await AllowRequestsAsync(stoppingToken);
        }

        var next = NextDailyCheck(DateTimeOffset.Now, Random.Shared);
        await RunAsync("the service started", stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_poll, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (TakeRequest())
            {
                await RunAsync("somebody asked", stoppingToken);
            }
            else if (DateTimeOffset.Now >= next)
            {
                next = NextDailyCheck(DateTimeOffset.Now, Random.Shared);
                await RunAsync("the daily check", stoppingToken);
            }
        }
    }

    /// <summary>
    /// A PC upgraded from a zip install still has the SYSTEM scheduled task that ran the old updater.
    /// Left alone it would go on replacing files Windows Installer now owns, so the first start after
    /// the upgrade deletes it. Absence is the ordinary answer on every other machine, not a failure.
    /// </summary>
    internal async Task RemoveLegacyTaskAsync(CancellationToken ct)
    {
        try
        {
            var result = await processes.RunAsync("schtasks.exe", $"/Delete /TN \"{LegacyTaskName}\" /F", null, TimeSpan.FromSeconds(30), ct);
            if (result.ExitCode == 0)
            {
                logger.LogInformation("Removed the leftover {Task} scheduled task", LegacyTaskName);
            }
            else
            {
                logger.LogInformation("No leftover {Task} scheduled task to remove", LegacyTaskName);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("Could not look for the {Task} scheduled task ({Reason})", LegacyTaskName, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Lets a signed-in user drop update.request in the state folder, and nothing else. The MSI gives
    /// Users read and execute there, which is what client.json needs and leaves them no way to ask for
    /// an update. The grant is on the folder itself with no inheritance, so it permits creating a file
    /// and confers nothing at all on the files already in it.
    /// </summary>
    internal async Task AllowRequestsAsync(CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(paths.StateDir);
            var result = await processes.RunAsync("icacls.exe", $"\"{paths.StateDir}\" /grant {UsersSid}:(WD)", null, TimeSpan.FromSeconds(30), ct);
            if (result.ExitCode != 0)
            {
                logger.LogWarning("Users may not be able to ask for an update; icacls exited {Code}", result.ExitCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not let users ask for an update ({Reason})", ex.GetType().Name);
        }
    }

    /// <summary>
    /// True when a client left a request behind. The file goes first, so a request made while the pass
    /// runs is answered by the next one rather than lost or run twice.
    /// </summary>
    private bool TakeRequest()
    {
        try
        {
            if (!File.Exists(paths.RequestPath))
            {
                return false;
            }

            File.Delete(paths.RequestPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not take the update request ({Reason})", ex.GetType().Name);
            return false;
        }
    }

    private async Task RunAsync(string reason, CancellationToken ct)
    {
        try
        {
            logger.LogInformation("Checking for an update because {Reason}", reason);
            var status = await update.RunAsync(ct);
            logger.LogInformation("Update check says {Result}", status.Result);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The service is stopping. The next start checks again.
        }
    }
}
