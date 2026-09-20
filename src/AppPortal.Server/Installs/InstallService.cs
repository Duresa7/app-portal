using System.Collections.Concurrent;

using AppPortal.Server.Action1;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Options;
using AppPortal.Shared;

using Microsoft.Extensions.Options;

namespace AppPortal.Server.Installs;

public enum InstallRejection
{
    UnknownApp,
    AlreadyInProgress,
    TooManyActive,
    PackageVersionNotFound,
}

public sealed class InstallRejectedException(InstallRejection reason, string message) : Exception(message)
{
    public InstallRejection Reason { get; } = reason;
}

/// <summary>Turns a device's install request into an Action1 deployment and keeps the record's state current.</summary>
public sealed class InstallService(
    CatalogStore catalog,
    InstallStore store,
    IAction1Client action1,
    IOptions<PortalOptions> options,
    ILogger<InstallService> logger)
{
    /// <summary>
    /// One gate per device. Without it two overlapping requests both read a snapshot that shows no
    /// install in flight, both pass the duplicate and concurrency checks, and both start an Action1
    /// deployment.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DeviceGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<InstallRecord> CreateAsync(DeviceRecord device, string appId, string? requestedBy, CancellationToken ct)
    {
        var gate = DeviceGates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            return await CreateCoreAsync(device, appId, requestedBy, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<InstallRecord> CreateCoreAsync(DeviceRecord device, string appId, string? requestedBy, CancellationToken ct)
    {
        var app = catalog.Find(appId)
                  ?? throw new InstallRejectedException(InstallRejection.UnknownApp, $"'{appId}' is not in the catalog.");

        var existing = store.ForDeviceId(device.Id);
        if (existing.Any(r => r.IsActive && string.Equals(r.AppId, app.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InstallRejectedException(InstallRejection.AlreadyInProgress, $"{app.Name} is already being installed on this device.");
        }

        if (existing.Count(r => r.IsActive) >= options.Value.MaxActiveInstallsPerDevice)
        {
            throw new InstallRejectedException(InstallRejection.TooManyActive, "Too many installs are already in progress on this device. Wait for one to finish.");
        }

        var version = await action1.ResolvePackageVersionAsync(app.Action1.PackageId, app.Action1.Version, ct)
                      ?? throw new InstallRejectedException(InstallRejection.PackageVersionNotFound, $"No published version of {app.Name} matches '{app.Action1.Version}' in the Software Repository.");

        var record = new InstallRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            DeviceId = device.Id,
            DeviceName = device.Name,
            EndpointId = device.EndpointId,
            AppId = app.Id,
            AppName = app.Name,
            RequestedBy = requestedBy,
            PackageId = app.Action1.PackageId,
            Version = version.Version,
            RequestedAt = DateTimeOffset.UtcNow,
            State = InstallState.Queued,
            Detail = "Sent to the management service.",
        };

        var automationName = $"App Portal: {app.Name} {version.Version} on {device.Name}";
        record.AutomationId = await action1.StartDeploymentAsync(device.EndpointId, automationName, app.Action1.PackageId, version.Version, $"{app.Name} {version.Version}", ct);
        store.Upsert(record);
        logger.LogInformation("Device {Device} requested {App} {Version} for {User}; automation {Automation}",
            device.Name, app.Name, version.Version, requestedBy ?? "an unnamed account", record.AutomationId);
        return record;
    }

    public async Task<InstallRecord> RefreshAsync(InstallRecord record, CancellationToken ct)
    {
        if (!record.IsActive || record.AutomationId is null)
        {
            return record;
        }

        Action1DeploymentStatus status;
        try
        {
            status = await action1.GetDeploymentStatusAsync(record.AutomationId, record.EndpointId, ct);
        }
        catch (Action1Exception ex)
        {
            logger.LogWarning(ex, "Could not read status for automation {Automation}", record.AutomationId);
            record.LastCheckedAt = DateTimeOffset.UtcNow;
            store.Upsert(record);
            return record;
        }

        record.LastCheckedAt = DateTimeOffset.UtcNow;
        record.PercentComplete = status.PercentComplete;
        record.Detail = status.Detail ?? record.Detail;
        var previous = record.State;
        record.State = status.Status switch
        {
            "Pending" => InstallState.Queued,
            "Running" => InstallState.Running,
            "Success" => InstallState.Succeeded,
            "Warning" => InstallState.Succeeded,
            "Error" => InstallState.Failed,
            "Stopped" => InstallState.Cancelled,
            _ => record.State,
        };

        if (!record.IsActive)
        {
            record.CompletedAt ??= DateTimeOffset.UtcNow;
            record.PercentComplete = record.State == InstallState.Succeeded ? 100 : record.PercentComplete;
            if (status.Status == "Warning")
            {
                record.Detail = "Installed with warnings. " + (status.Detail ?? "");
            }
        }

        if (previous != record.State)
        {
            logger.LogInformation("Install {Id} for {Device} moved {From} -> {To}", record.Id, record.DeviceName, previous, record.State);
        }

        store.Upsert(record);
        return record;
    }

    public async Task<IReadOnlyList<InstallRecord>> ListForDeviceAsync(DeviceRecord device, bool refreshActive, CancellationToken ct)
    {
        var records = store.ForDeviceId(device.Id);
        if (!refreshActive)
        {
            return records;
        }

        var refreshed = new List<InstallRecord>(records.Count);
        foreach (var record in records)
        {
            refreshed.Add(record.IsActive ? await RefreshAsync(record, ct) : record);
        }

        return refreshed;
    }

    public async Task<IReadOnlyList<InstalledApp>> InstalledAppsAsync(DeviceRecord device, CancellationToken ct)
    {
        var inventory = await action1.GetInstalledSoftwareAsync(device.EndpointId, ct);
        var entries = catalog.Entries;
        return inventory
            .Select(item => new InstalledApp(item.Name, item.Vendor, item.Version,
                entries.FirstOrDefault(e => e.MatchesInstalled(item.Name))?.Id))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
