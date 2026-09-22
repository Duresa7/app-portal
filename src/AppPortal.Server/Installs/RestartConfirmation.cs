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

        // Per account, not one list for the device: software installed into somebody's profile is
        // reported under their name, so looking only at the machine-wide list would find nothing and
        // call every per-user install that needed a restart a failure.
        //
        // How fresh each list is differs, and the difference is worth knowing. The machine-wide one is
        // swept when the agent starts, which a restart guarantees, so it describes the PC as it is now.
        // A person's own list cannot be: the sweep would have to run inside their session, and they may
        // not have signed in yet. Theirs is as fresh as their last install, which is the moment before
        // the restart. An empty list counts as found for exactly this reason.
        var byAccount = new Dictionary<string, IReadOnlyList<ReportedSoftware>>(StringComparer.OrdinalIgnoreCase);
        var entries = catalog.Entries;
        foreach (var install in waiting)
        {
            var account = install.RequestedBy ?? "";
            if (!byAccount.TryGetValue(account, out var present))
            {
                byAccount[account] = present = software.ForDevice(device.Id, account);
            }

            var entry = entries.FirstOrDefault(e => e.Id == install.AppId);
            var found = present.Any(item => entry?.MatchesInstalled(item.Name, item.Source)
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
