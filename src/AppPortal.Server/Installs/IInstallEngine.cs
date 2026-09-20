using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Shared;

namespace AppPortal.Server.Installs;

public interface IInstallEngine
{
    string Name { get; }

    // The definition is null for an engine that reads its own package off the catalog entry, which is
    // what Action1 does. Only the agent is handed one.
    Task<string> StartAsync(DeviceRecord device, CatalogEntry app, PackageDefinition? definition, InstallRecord install, CancellationToken ct);

    Task<InstallRecord> RefreshAsync(InstallRecord install, CancellationToken ct);
}
