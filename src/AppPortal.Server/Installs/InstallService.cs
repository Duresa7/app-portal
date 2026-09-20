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

/// <summary>Routes a device's install request and keeps the record's state current.</summary>
public sealed class InstallService(
    CatalogStore catalog,
    InstallStore store,
    IAction1Client action1,
    IOptions<PortalOptions> options,
    ILogger<InstallService> logger,
    IEnumerable<IInstallEngine> engines,
    DeviceSoftwareStore software)
{
    private readonly Dictionary<string, IInstallEngine> _engines = engines.ToDictionary(e => e.Name);

    /// <summary>
    /// One gate per device. Without it two overlapping requests both read a snapshot that shows no
    /// install in flight, both pass the duplicate and concurrency checks, and both start an
    /// install.
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

        var definition = device.HasAgent ? store.FindAgentOnlyPackage(app.Id) : null;
        var engine = _engines[definition is null ? EngineLabel.Action1 : EngineLabel.Agent];

        var record = new InstallRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            DeviceId = device.Id,
            DeviceName = device.Name,
            EndpointId = device.EndpointId,
            AppId = app.Id,
            AppName = app.Name,
            RequestedBy = requestedBy,
            RequestedAt = DateTimeOffset.UtcNow,
            State = InstallState.Queued,
            Engine = definition is null ? EngineLabel.Action1 : EngineLabel.Agent,
        };

        record.AutomationId = await engine.StartAsync(device, app, definition, record, ct);
        logger.LogInformation("Device {Device} requested {App} for {User}; engine {Engine}, reference {Reference}",
            device.Name, app.Name, requestedBy ?? "an unnamed account", record.Engine, record.AutomationId);
        return record;
    }

    public Task<InstallRecord> RefreshAsync(InstallRecord record, CancellationToken ct)
    {
        var engine = _engines[record.Engine];
        return engine.RefreshAsync(record, ct);
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

    public async Task<IReadOnlyList<InstalledApp>> InstalledAppsAsync(DeviceRecord device, CancellationToken ct, string? requester = null)
    {
        var entries = catalog.Entries;
        var merged = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        // A device with no Action1 endpoint has nothing to ask, and asking anyway fails the whole call
        // for a device whose only engine is the agent.
        if (!string.IsNullOrWhiteSpace(device.EndpointId))
        {
            foreach (var item in await action1.GetInstalledSoftwareAsync(device.EndpointId, ct))
            {
                merged[item.Name] = new InstalledApp(item.Name, item.Vendor, item.Version,
                    entries.FirstOrDefault(e => e.MatchesInstalled(item.Name))?.Id);
            }
        }

        // Action1 reports a vendor and the agent cannot, so where both saw the same software the richer
        // row stays and the agent's is dropped rather than overwriting it with a blank.
        foreach (var item in software.ForDevice(device.Id, requester).Where(item => !merged.ContainsKey(item.Name)))
        {
            merged[item.Name] = new InstalledApp(item.Name, "", item.Version,
                entries.FirstOrDefault(e => e.MatchesInstalled(item.Name))?.Id);
        }

        return merged.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
