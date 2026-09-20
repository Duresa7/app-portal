using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Shared;

namespace AppPortal.Server.Installs;

/// <summary>
/// Closes out the installs that were only waiting for the PC to restart. The agent says when the
/// device last booted; anything that was waiting before that boot has had its restart, and what
/// remains is whether the software is still there.
/// </summary>
public sealed class RestartConfirmation(
    InstallStore installs,
    DeviceSoftwareStore software,
    CatalogStore catalog,
    ILogger<RestartConfirmation> logger)
{
    /// <summary>
    /// Settles every install on this device that was waiting for a restart older than this boot.
    /// Returns how many it settled, which is only of interest to a test.
    /// </summary>
    public int Apply(DeviceRecord device, DateTimeOffset bootTime)
    {
        var waiting = installs.ForDeviceId(device.Id)
            // Last written rather than completed: an install waiting for a restart is still running, so
            // it has no completion time yet. Nothing else touches the row while it waits, which makes
            // this the moment it started waiting.
            .Where(install => install.IsWaitingForRestart && install.LastCheckedAt is { } waitingSince && waitingSince < bootTime)
            .ToList();
        if (waiting.Count == 0)
        {
            return 0;
        }

        var present = software.ForDevice(device.Id);
        var entries = catalog.Entries;
        foreach (var install in waiting)
        {
            // The agent sweeps what is installed when it starts, so this list is what the device looks
            // like after the restart rather than before it.
            var entry = entries.FirstOrDefault(e => e.Id == install.AppId);
            var found = present.Any(item => entry?.MatchesInstalled(item.Name)
                                            ?? item.Name.Contains(install.AppName, StringComparison.OrdinalIgnoreCase));

            install.RebootState = RebootState.Confirmed;
            install.LastCheckedAt = DateTimeOffset.UtcNow;
            if (found || present.Count == 0)
            {
                // Nothing reported at all is not the same as reporting the software is gone. A device
                // whose agent cannot read its own software list must not have its history rewritten.
                install.State = InstallState.Succeeded;
                install.PercentComplete = 100;
                install.Detail = "Installed. The PC has restarted.";
            }
            else
            {
                install.State = InstallState.Failed;
                install.Detail = "The software was not there after the restart.";
                logger.LogWarning("Install {Id} of {App} was gone after {Device} restarted", install.Id, install.AppName, device.Name);
            }

            install.CompletedAt ??= DateTimeOffset.UtcNow;
            installs.Upsert(install);
        }

        return waiting.Count;
    }
}
