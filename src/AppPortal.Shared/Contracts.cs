namespace AppPortal.Shared;

/// <summary>One application the administrator has approved for self-service installation.</summary>
public sealed record CatalogApp(
    string Id,
    string Name,
    string Publisher,
    string Description,
    string Category,
    string? IconUrl,
    bool Featured);

/// <summary>The device the caller authenticated as, plus what the management plane knows about it.</summary>
public sealed record DeviceInfo(
    string DeviceName,
    string EndpointId,
    string EndpointStatus,
    DateTimeOffset? LastSeen);

public enum InstallState
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>One installation request raised from a device, and where it stands.</summary>
public sealed record InstallRequest(
    string Id,
    string AppId,
    string AppName,
    string DeviceName,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    InstallState State,
    int PercentComplete,
    string? Detail);

/// <summary>Software the management plane reports as present on the device.</summary>
public sealed record InstalledApp(
    string Name,
    string Vendor,
    string Version,
    string? CatalogAppId);

public sealed record CreateInstallRequest(string AppId);

public sealed record ErrorMessage(string Message);

public static class ApiRoutes
{
    public const string Prefix = "/api/v1";
    public const string Catalog = Prefix + "/catalog";
    public const string Device = Prefix + "/device";
    public const string Installed = Prefix + "/device/installed";
    public const string Installs = Prefix + "/installs";
}
