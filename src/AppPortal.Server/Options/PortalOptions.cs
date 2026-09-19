namespace AppPortal.Server.Options;

public sealed class PortalOptions
{
    public const string Section = "Portal";

    public string CatalogPath { get; set; } = "config/catalog.json";

    public string DevicesPath { get; set; } = "config/devices.json";

    public string DataDirectory { get; set; } = "data";

    public int StatusPollSeconds { get; set; } = 30;

    /// <summary>Installs a single device may have in flight at once.</summary>
    public int MaxActiveInstallsPerDevice { get; set; } = 3;
}
