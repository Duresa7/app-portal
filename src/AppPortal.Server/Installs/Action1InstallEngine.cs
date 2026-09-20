using AppPortal.Server.Action1;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Shared;

namespace AppPortal.Server.Installs;

public sealed class Action1InstallEngine(IAction1Client action1, InstallStore store, ILogger<Action1InstallEngine> logger) : IInstallEngine
{
    public string Name => EngineLabel.Action1;

    public async Task<string> StartAsync(DeviceRecord device, CatalogEntry app, PackageDefinition definition, InstallRecord record, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(app.Action1.PackageId))
        {
            throw new InstallRejectedException(InstallRejection.PackageVersionNotFound, $"{app.Name} has no Action1 package for this device.");
        }

        var version = await action1.ResolvePackageVersionAsync(app.Action1.PackageId, app.Action1.Version, ct)
                      ?? throw new InstallRejectedException(InstallRejection.PackageVersionNotFound, $"No published version of {app.Name} matches '{app.Action1.Version}' in the Software Repository.");
        record.PackageId = app.Action1.PackageId;
        record.Version = version.Version;
        record.Detail = "Sent to the management service.";
        var automationName = $"App Portal: {app.Name} {version.Version} on {device.Name}";
        record.AutomationId = await action1.StartDeploymentAsync(device.EndpointId, automationName, app.Action1.PackageId, version.Version, $"{app.Name} {version.Version}", ct);
        store.Upsert(record);
        return record.AutomationId;
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

}
