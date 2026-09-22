using AppPortal.Agent.Executors;
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
    IUninstallRegistry registry,
    ILogger<UpdateWorker> logger,
    TimeSpan? pollInterval = null,
    bool? windows = null) : BackgroundService
{
    /// <summary>The scheduled task the zip updater installed, which an MSI upgrade has to be rid of.</summary>
    public const string LegacyTaskName = "App Portal Updater";

    /// <summary>
    /// The uninstall entry the zip installer wrote by hand. Its UninstallString still runs the retired
    /// script, which deletes the install folder and the state folder with it.
    /// </summary>
    public const string LegacyUninstallKey = "AppPortalClient";

    /// <summary>The SID of the local Users group, which is the same on every Windows in every language.</summary>
    private const string UsersSid = "*S-1-5-32-545";

    /// <summary>
    /// What a zip install left in the folder the MSI now owns: the updater the scheduled task ran, and
    /// the script the uninstall entry pointed at.
    /// </summary>
    private static readonly string[] LegacyFiles = ["AppPortal.Updater.exe", "Uninstall-AppPortalClient.ps1"];

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
            await RemoveLegacyInstallAsync(stoppingToken);
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
    /// A PC upgraded from a zip install carries three things Windows Installer knows nothing about: the
    /// SYSTEM scheduled task that ran the old updater, the updater and its uninstall script in the folder
    /// the MSI now owns, and an uninstall entry of its own. The entry is the one that hurts. It sits in
    /// Apps &amp; Features beside the MSI's under the same name, and the script behind it deletes both the
    /// install folder and the state folder, so whoever picks the wrong row of two identical ones leaves
    /// Windows Installer holding a product whose files are gone. The first start after the upgrade takes
    /// all three away. Absence is the ordinary answer on every machine that was installed from an MSI in
    /// the first place, and none of it is worth a failed start.
    /// </summary>
    internal async Task RemoveLegacyInstallAsync(CancellationToken ct)
    {
        await RemoveLegacyTaskAsync(ct);
        RemoveLegacyUninstallEntry();
        RemoveLegacyFiles();
    }

    /// <summary>
    /// Left alone the task would go on replacing files Windows Installer now owns.
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
    /// Only the key the zip installer wrote, by the name it wrote it under. The MSI's own entry lives
    /// under its product code and is what uninstalling this product is supposed to go through.
    /// </summary>
    private void RemoveLegacyUninstallEntry()
    {
        try
        {
            if (registry.Remove(LegacyUninstallKey))
            {
                logger.LogInformation("Removed the leftover {Key} uninstall entry", LegacyUninstallKey);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not remove the {Key} uninstall entry ({Reason})", LegacyUninstallKey, ex.GetType().Name);
        }
    }

    /// <summary>
    /// A file a stale process still holds open cannot be deleted, and that is worth saying out loud and
    /// nothing more: the updater without its task and its entry has no way left to run.
    /// </summary>
    private void RemoveLegacyFiles()
    {
        foreach (var name in LegacyFiles)
        {
            var path = Path.Combine(paths.InstallDir, name);
            try
            {
                // The check is for the log line alone. File.Delete says nothing about a file that was
                // never there, which is what this folder holds on a machine the MSI installed.
                var there = File.Exists(path);
                File.Delete(path);
                if (there)
                {
                    logger.LogInformation("Removed {File}, left behind by a zip install", name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Could not remove {File}, left behind by a zip install ({Reason})", name, ex.GetType().Name);
            }
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
