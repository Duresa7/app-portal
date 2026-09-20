using AppPortal.Shared;

namespace AppPortal.Agent.Jobs;

/// <summary>
/// What is on this PC, reported once when the service starts.
/// </summary>
/// <remarks>
/// Until this existed the only sweep ran straight after an install the agent had just done, which left
/// two things wrong. An install that finishes at a restart was checked against the list from before the
/// restart, so the check could never see the thing it exists to look for. And a device whose only
/// engine is the agent had nowhere else for inventory to come from, so after an upgrade, or on any PC
/// that had never installed anything through the portal, its Installed list stayed empty and its
/// prerequisites looked unmet.
///
/// A service start is the one moment that answers both: it happens after every restart, and it happens
/// after every upgrade, because Windows Installer restarts the service on its way through.
/// </remarks>
public sealed class StartupSoftwareSweep(
    SoftwareReporter software,
    ILogger<StartupSoftwareSweep> logger,
    Func<PortalSettings>? loadSettings = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = (loadSettings ?? (() => PortalSettings.Load()))();
        if (!settings.IsConfigured)
        {
            // Not enrolled yet. The install that enrolls this PC sweeps when it finishes, and the next
            // start sweeps again.
            return;
        }

        try
        {
            logger.LogInformation("Reporting what is installed on this device");
            await software.ReportAsync(settings, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopped before winget answered. The next start asks again, and nothing here is worth
            // taking the host down for.
        }
    }
}
