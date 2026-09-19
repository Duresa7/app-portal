namespace AppPortal.Server.Options;

public sealed class PortalOptions
{
    public const string Section = "Portal";

    public string CatalogPath { get; set; } = "config/catalog.json";

    /// <summary>Device registry. This is mutable state, so it belongs with the data, not with the read-only catalog.</summary>
    public string DevicesPath { get; set; } = "data/devices.json";

    public string DataDirectory { get; set; } = "data";

    public int StatusPollSeconds { get; set; } = 30;

    /// <summary>Installs a single device may have in flight at once.</summary>
    public int MaxActiveInstallsPerDevice { get; set; } = 3;
}
