namespace AppPortal.Shared;

/// <summary>One application the administrator has approved for self-service installation.</summary>
[method: System.Text.Json.Serialization.JsonConstructor]
public sealed record CatalogApp(
    string Id,
    string Name,
    string Publisher,
    string Description,
    string Category,
    string? IconUrl,
    bool Featured,
    string[] Engines,
    long? DownloadSizeBytes)
{
    public CatalogApp(string id, string name, string publisher, string description, string category, string? iconUrl, bool featured)
        : this(id, name, publisher, description, category, iconUrl, featured, ["action1"], null)
    {
    }
}

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
    string? Detail,
    string? RequestedBy);

/// <summary>Software the management plane reports as present on the device.</summary>
public sealed record InstalledApp(
    string Name,
    string Vendor,
    string Version,
    string? CatalogAppId);

public sealed record CreateInstallRequest(string AppId);

public enum AppRequestStatus
{
    Pending,
    Approved,
    Denied,
}

/// <summary>Something a user asked for that is not in the catalog, and what an administrator decided.</summary>
public sealed record AppRequest(
    string Id,
    string Text,
    string DeviceName,
    string? RequestedBy,
    AppRequestStatus Status,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);

public sealed record CreateAppRequest(string Text);

/// <summary>
/// What a PC presents to turn an enrollment key into a device of its own. Sent without a bearer token:
/// the key is what authenticates the call, and the token that comes back is what authenticates the next.
/// </summary>
public sealed record EnrollRequest(
    string Key,
    string DeviceName,
    string MachineId,
    string? Action1EndpointId,
    string? AgentVersion);

/// <summary>
/// The device a successful enrollment created or took over. <see cref="DeviceToken"/> is shown once,
/// here, and is never recoverable from the server afterwards.
/// </summary>
public sealed record EnrollResponse(
    string DeviceId,
    string DeviceToken,
    string DeviceName,
    IReadOnlyList<string> Engines);

public sealed record ErrorMessage(string Message);

public static class AppRequestLimits
{
    /// <summary>The longest request text the server stores, and what the client's box allows.</summary>
    public const int MaxTextLength = 500;

    /// <summary>How many undecided requests one device may have before it must wait for an answer.</summary>
    public const int MaxPendingPerDevice = 20;
}

public static class ApiHeaders
{
    /// <summary>
    /// The signed-in Windows account the client is acting for, as <c>DOMAIN\user</c>. Informational only:
    /// the device token is what authenticates the call, and the account is trusted because the PC is managed.
    /// </summary>
    public const string Requester = "X-AppPortal-User";

    /// <summary>The longest account name the server stores; anything past this is cut off.</summary>
    public const int RequesterMaxLength = 128;

    /// <summary>
    /// The enrollment key on <c>GET /api/v1/enroll/check</c>. A header rather than a query parameter so
    /// the secret stays out of access logs and browser history.
    /// </summary>
    public const string EnrollmentKey = "X-Enrollment-Key";
}

public static class ApiRoutes
{
    public const string Prefix = "/api/v1";
    public const string Catalog = Prefix + "/catalog";
    public const string Device = Prefix + "/device";
    public const string Installed = Prefix + "/device/installed";
    public const string Installs = Prefix + "/installs";
    public const string Requests = Prefix + "/requests";
    public const string Enroll = Prefix + "/enroll";
    public const string EnrollCheck = Enroll + "/check";
}

/// <summary>What the agent reports on each heartbeat. The client version is null when none is installed.</summary>
public sealed record AgentHeartbeatRequest(string AgentVersion, string? ClientVersion, string OsVersion);

/// <summary>
/// The answer to a heartbeat. <see cref="HeartbeatSeconds"/> is how long the agent should wait before
/// the next one, so a fleet that is calling in too often can be slowed down without shipping a build.
/// </summary>
public sealed record AgentHeartbeatResponse(DateTimeOffset ServerTime, int HeartbeatSeconds);
