using AppPortal.Server.Agent;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Shared;

namespace AppPortal.Server.Installs;

public sealed class AgentInstallEngine(AgentJobStore jobs, InstallStore installs) : IInstallEngine
{
    public string Name => EngineLabel.Agent;

    public Task<string> StartAsync(DeviceRecord device, CatalogEntry app, PackageDefinition definition, InstallRecord install, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(jobs.Create(install, definition));
    }

    public Task<InstallRecord> RefreshAsync(InstallRecord install, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        jobs.RequeueExpired();
        return Task.FromResult(installs.Find(install.Id) ?? install);
    }
}
