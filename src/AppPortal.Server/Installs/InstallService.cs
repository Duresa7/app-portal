using System.Collections.Concurrent;

using AppPortal.Server.Action1;
using AppPortal.Server.Agent;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Options;
using AppPortal.Server.Settings;
using AppPortal.Shared;

using Microsoft.Extensions.Options;

namespace AppPortal.Server.Installs;

public enum InstallRejection
{
    UnknownApp,
    AlreadyInProgress,
    TooManyActive,
    PackageVersionNotFound,

    /// <summary>The caller may not do this, whatever the state of the device.</summary>
    NotAllowed,
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
    DeviceSoftwareStore software,
    SettingsStore settings,
    PrerequisiteStore prerequisites,
    InstallStepStore steps,
    DeviceStore devices,
    AgentJobStore jobs)
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

    /// <summary>
    /// Stops an install that has not finished. Only an install the agent is carrying out can be
    /// stopped here: Action1 owns what Action1 started, and reaching around it would leave the two
    /// disagreeing about what is running.
    /// </summary>
    public InstallRecord Cancel(string installId, string by)
    {
        var record = store.Find(installId)
                     ?? throw new InstallRejectedException(InstallRejection.UnknownApp, "No such install.");
        if (!record.IsActive)
        {
            throw new InstallRejectedException(InstallRejection.NotAllowed,
                $"{record.AppName} has already finished, so there is nothing to stop.");
        }

        if (record.Engine != EngineLabel.Agent)
        {
            throw new InstallRejectedException(InstallRejection.NotAllowed,
                $"{record.AppName} is being installed by Action1 and has to be stopped there.");
        }

        if (!jobs.Cancel(installId, $"Stopped by {by}."))
        {
            throw new InstallRejectedException(InstallRejection.NotAllowed,
                $"{record.AppName} finished before it could be stopped.");
        }

        logger.LogInformation("Install {Install} stopped by {By}", installId, by);
        return store.Find(installId)!;
    }

    /// <summary>
    /// Takes software off a device. Allowed for the person who asked for it when the administrator has
    /// said the app may be removed, and for an administrator whatever the app says.
    /// </summary>
    public async Task<InstallRecord> UninstallAsync(DeviceRecord device, string appId, string? requestedBy,
        bool asAdministrator, CancellationToken ct)
    {
        var app = catalog.Find(appId)
                  ?? throw new InstallRejectedException(InstallRejection.UnknownApp, $"'{appId}' is not in the catalog.");
        if (!asAdministrator && !app.UserRemovable)
        {
            throw new InstallRejectedException(InstallRejection.NotAllowed,
                $"{app.Name} can only be removed by an administrator.");
        }

        var history = store.ForDeviceId(device.Id);
        if (history.Any(r => r.IsActive && string.Equals(r.AppId, app.Id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InstallRejectedException(InstallRejection.AlreadyInProgress, $"{app.Name} is already being worked on for this device.");
        }

        if (!asAdministrator)
        {
            // Somebody may remove what they installed, and a machine-wide install is everybody's.
            var theirs = history.Any(r => !r.IsUninstall
                                          && string.Equals(r.AppId, app.Id, StringComparison.OrdinalIgnoreCase)
                                          && r.State == InstallState.Succeeded
                                          && (r.RequestedBy is null
                                              || string.Equals(r.RequestedBy, requestedBy, StringComparison.OrdinalIgnoreCase)
                                              || app.Agent?.Scope != "user"));
            if (!theirs)
            {
                throw new InstallRejectedException(InstallRejection.NotAllowed,
                    $"{app.Name} was installed for somebody else on this PC.");
            }
        }

        var chosen = EngineSelector.Choose(device, app, settings.DefaultEngine)
                     ?? throw new InstallRejectedException(InstallRejection.PackageVersionNotFound,
                         $"{app.Name} cannot be removed from this PC.");
        if (chosen != EngineLabel.Agent)
        {
            // Action1 owns what Action1 deployed, and reaching around it would leave the two disagreeing.
            throw new InstallRejectedException(InstallRejection.NotAllowed,
                $"{app.Name} is managed by Action1 on this PC and has to be removed there.");
        }

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
            Engine = chosen,
            Kind = InstallKind.Uninstall,
            StepName = app.Name,
            StepNumber = 1,
            StepCount = 1,
        };

        record.AutomationId = await StartStepAsync(device, record, app, chosen, 0, ct);
        logger.LogInformation("Device {Device} asked to remove {App} for {User}", device.Name, app.Name, requestedBy ?? "an unnamed account");
        return record;
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

        // What has to happen, in order, ending with the app somebody actually asked for. An app that
        // needs nothing first yields a chain of one, so there is no second code path to keep in step.
        var installed = software.ForDevice(device.Id, requestedBy);
        var chain = PrerequisiteResolver.Expand(app, catalog.Entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase),
            prerequisites.All(), entry => installed.Any(item => entry.MatchesInstalled(item.Name, item.Source)));

        var plan = new List<(CatalogEntry App, string Engine)>();
        foreach (var entry in chain)
        {
            // Each step is routed on its own, so a chain may run partly through one engine and partly
            // through the other. A step nothing can install makes the whole chain impossible.
            var stepEngine = EngineSelector.Choose(device, entry, settings.DefaultEngine)
                             ?? throw new InstallRejectedException(InstallRejection.PackageVersionNotFound,
                                 entry.Id == app.Id
                                     ? $"{app.Name} cannot be installed on this PC."
                                     : $"{app.Name} needs {entry.Name} first, and that cannot be installed on this PC.");
            plan.Add((entry, stepEngine));
        }

        var first = plan[0];
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
            Engine = first.Engine,
            StepName = first.App.Name,
            StepNumber = 1,
            StepCount = plan.Count,
        };

        // The first step is started before the rest are written down, because starting it is what
        // creates the install row the steps point at.
        record.AutomationId = await StartStepAsync(device, record, first.App, first.Engine, 0, ct);
        steps.Add([.. plan.Skip(1).Select((step, index) => new InstallStep
        {
            InstallId = record.Id,
            Position = index + 1,
            AppId = step.App.Id,
            AppName = step.App.Name,
            Engine = step.Engine,
            State = InstallState.Queued,
        })]);

        logger.LogInformation("Device {Device} requested {App} for {User}; {Steps} step(s), engine {Engine}, reference {Reference}",
            device.Name, app.Name, requestedBy ?? "an unnamed account", plan.Count, record.Engine, record.AutomationId);
        return record;
    }

    /// <summary>Starts one step and records its reference, so a refresh can find it again.</summary>
    private async Task<string?> StartStepAsync(DeviceRecord device, InstallRecord record, CatalogEntry app,
        string engineName, int position, CancellationToken ct)
    {
        var definition = engineName == EngineLabel.Agent ? store.FindAgentPackage(app.Id) : null;
        var reference = await _engines[engineName].StartAsync(device, app, definition, record, ct);
        steps.Update(new InstallStep
        {
            InstallId = record.Id,
            Position = position,
            AppId = app.Id,
            AppName = app.Name,
            Engine = engineName,
            ExternalRef = reference,
            State = InstallState.Running,
        });
        return reference;
    }

    public async Task<InstallRecord> RefreshAsync(InstallRecord record, CancellationToken ct)
    {
        var refreshed = await _engines[record.Engine].RefreshAsync(record, ct);
        return await AdvanceAsync(refreshed, ct);
    }

    /// <summary>
    /// Moves a chained install on to its next step when the step it was on has finished. Driven from
    /// the refresh the client already makes rather than from the completion itself: the step that
    /// finishes does so inside the job store's own transaction, and starting the next one from in
    /// there would mean an install engine reaching back into a write that has not been committed.
    /// </summary>
    private async Task<InstallRecord> AdvanceAsync(InstallRecord record, CancellationToken ct)
    {
        var chain = steps.For(record.Id);
        if (chain.Count <= 1)
        {
            return record;
        }

        var running = chain.FirstOrDefault(step => step.ExternalRef == record.AutomationId);
        if (running is null)
        {
            return record;
        }

        // The step's own outcome where the job store wrote one, and the install's otherwise: an
        // Action1 step is settled by the poller, which knows nothing about chains.
        var outcome = running.State is InstallState.Succeeded or InstallState.Failed or InstallState.Cancelled
            ? running.State
            : record.State;
        running.State = outcome;
        running.Detail ??= record.Detail;
        steps.Update(running);
        record.StepCount = chain.Count;
        record.StepNumber = running.Position + 1;
        record.StepName = running.AppName;

        if (outcome is InstallState.Failed or InstallState.Cancelled)
        {
            // The rest are not attempted. Naming the step is the whole value of the message: "failed"
            // on a three-app chain otherwise tells nobody which of the three to look at.
            record.State = outcome;
            record.Detail = $"Step {running.Position + 1} of {chain.Count}, {running.AppName}: {running.Detail}";
            record.CompletedAt ??= DateTimeOffset.UtcNow;
            store.Upsert(record);
            return record;
        }

        if (outcome != InstallState.Succeeded || record.IsWaitingForRestart)
        {
            // A step that has to restart the PC first keeps the chain where it is until it has.
            return record;
        }

        var next = chain.FirstOrDefault(step => step.Position > running.Position);
        if (next is null)
        {
            return record;
        }

        var device = devices.Find(record.DeviceId ?? "")
                     ?? throw new InstallRejectedException(InstallRejection.UnknownApp, "The device is no longer known.");
        var app = catalog.Find(next.AppId)
                  ?? throw new InstallRejectedException(InstallRejection.UnknownApp, $"'{next.AppId}' has left the catalog.");

        record.State = InstallState.Running;
        record.PercentComplete = 0;
        record.Engine = next.Engine;
        record.CompletedAt = null;
        record.StepNumber = next.Position + 1;
        record.StepName = next.AppName;
        record.Detail = $"Installing {next.AppName} ({next.Position + 1} of {chain.Count})";

        // Written before the step is started, and with the previous step's reference cleared. A step
        // that settled the row, which an Action1 step does, leaves it saying the whole install
        // succeeded; the store refuses to move a settled install back to running unless the caller
        // says it means to, and the engine starting the next step writes through that same guard. So
        // the row is reopened first, and nothing in between names an engine and a reference that
        // belong to different steps.
        record.AutomationId = null;
        store.Upsert(record, reopening: true);
        record.AutomationId = await StartStepAsync(device, record, app, next.Engine, next.Position, ct);
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
                    entries.FirstOrDefault(e => e.MatchesInstalled(item.Name))?.Id, "action1");
            }
        }

        // Action1 reports a vendor and the agent cannot, so where both saw the same software the richer
        // row stays and the agent's is dropped rather than overwriting it with a blank. Two of the
        // agent's own sources are kept apart: Git from winget and git from Scoop are two copies.
        var agent = new Dictionary<(string Source, string Name), InstalledApp>();
        foreach (var item in software.ForDevice(device.Id, requester).Where(item => !merged.ContainsKey(item.Name)))
        {
            agent[(item.Source, item.Name.ToUpperInvariant())] = new InstalledApp(item.Name, "", item.Version,
                entries.FirstOrDefault(e => e.MatchesInstalled(item.Name, item.Source))?.Id, item.Source);
        }

        return merged.Values.Concat(agent.Values).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
