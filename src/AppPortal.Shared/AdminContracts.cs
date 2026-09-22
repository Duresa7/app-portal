namespace AppPortal.Shared;

/// <summary>
/// One page of an administration list. The caller asked for <paramref name="Limit"/> rows from
/// <paramref name="Offset"/>; the limit that comes back is the one the server settled on, which may be
/// smaller than the one that was asked for, so a client can page without guessing the server's ceiling.
/// </summary>
public sealed record AdminPage<T>(IReadOnlyList<T> Items, int Offset, int Limit, bool HasMore, int? Total);

/// <summary>One install or removal, as the fleet history shows it to an administrator.</summary>
public sealed record AdminInstall(
    string Id,
    string AppId,
    string AppName,
    string? DeviceId,
    string DeviceName,
    string EndpointId,
    string? RequestedBy,
    string Engine,
    string Kind,
    InstallState State,
    int PercentComplete,
    string? Detail,
    string? RebootState,
    string? StepName,
    int StepNumber,
    int StepCount,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? LastCheckedAt,
    string? AutomationId);

/// <summary>
/// A request for software, with the administrator who decided it. Wider than <see cref="AppRequest"/>,
/// which is what the requester's own device is shown: that one does not say who decided.
/// </summary>
public sealed record AdminRequest(
    string Id,
    string Text,
    string DeviceName,
    string? RequestedBy,
    AppRequestStatus Status,
    string? Reason,
    string? DecidedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DecidedAt);

/// <summary>Approving or denying a request. The reason is optional and reaches the person who asked.</summary>
public sealed record AdminDecision(string? Reason = null);

/// <summary>What an app matches in a device's installed-software list.</summary>
public sealed record AdminMatchRule(string? NameContains, string? NameEquals);

/// <summary>The Software Repository package an app deploys through Action1.</summary>
public sealed record AdminAction1Package(string PackageId, string Version = "latest");

/// <summary>
/// One catalog app in full, as the edit form holds it: every field an administrator can change, so a
/// read followed by a write round-trips without losing anything the form would have kept.
/// </summary>
public sealed record AdminCatalogApp(
    string Id,
    string Name,
    string Publisher = "",
    string Description = "",
    string Category = "Other",
    string? IconUrl = null,
    bool Featured = false,
    bool Hidden = false,
    string? EngineOverride = null,
    string? Requirements = null,
    IReadOnlyList<string>? Requires = null,
    bool UserRemovable = false,
    AdminMatchRule? Match = null,
    AdminAction1Package? Action1 = null,
    PackageDefinition? Agent = null);

/// <summary>Hiding an app takes it off devices and keeps its history.</summary>
public sealed record AdminCatalogHidden(bool Hidden);

/// <summary>How many apps an import wrote.</summary>
public sealed record AdminCatalogImported(int Imported);

public sealed record AdminPackageSearch(string? Term);

/// <summary>
/// A package to look up. <paramref name="Source"/> names which of winget's sources to ask, because a
/// Microsoft Store product id has no manifest in winget-pkgs and asking for one reports a correct id
/// as a missing one.
/// </summary>
public sealed record AdminPackageRef(string PackageId, string? Version = null, string? Source = null);

/// <summary>One Software Repository package a search found.</summary>
public sealed record AdminPackageResult(string Id, string Name, string Vendor, bool Builtin);

/// <summary>Whether a package and version resolve, and the wording to show either way.</summary>
public sealed record AdminPackageVerified(bool Ok, string Message);

/// <summary>Asking the server to download an installer and report what it is, for the direct package form.</summary>
public sealed record AdminInstallerRequest(string Url);

public sealed record AdminInstallerHash(string Sha256, long SizeBytes);

public sealed record AdminWingetLookup(bool? Exists, string Message);

/// <summary>One device in the fleet. The token is never here: it exists only in the reply that issued it.</summary>
public sealed record AdminDevice(
    string Id,
    string Name,
    string EndpointId,
    bool Enabled,
    bool HasAgent,
    string? EnginePreference,
    string? AgentVersion,
    string? EnrolledWithKeyId,
    string? EnrolledWithKeyName,
    string? MachineId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeenAt,
    int InstallCount);

/// <summary>A device and the recent history the device page shows beside it.</summary>
public sealed record AdminDeviceDetail(
    AdminDevice Device,
    IReadOnlyList<AdminInstall> RecentInstalls,
    IReadOnlyList<AdminRequest> RecentRequests,
    IReadOnlyList<DeviceManager>? Managers = null);

public sealed record AdminDeviceCreate(string Name, string? Action1EndpointId = null);

public sealed record AdminDeviceUpdate(
    string Name,
    string? Action1EndpointId = null,
    bool Enabled = true,
    string? EnginePreference = null);

/// <summary>A device token, shown once. The server keeps only its SHA-256 and cannot show it again.</summary>
public sealed record AdminDeviceToken(string DeviceId, string DeviceToken);

/// <summary>One enrollment key without its secret.</summary>
public sealed record EnrollmentKeySummary(
    string Id,
    string Name,
    string KeyPrefix,
    string DefaultEngine,
    string Status,
    DateTimeOffset? ExpiresAt,
    int? MaxUses,
    int Uses,
    DateTimeOffset? RevokedAt,
    string CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record EnrollmentKeyCreate(
    string Name,
    string Engine = "action1",
    DateTimeOffset? ExpiresAt = null,
    int? MaxUses = null);

/// <summary>
/// A new key and its plaintext. The plaintext exists in this one reply and nowhere else: it is not
/// stored, not logged, and cannot be shown again. Anything holding one of these must treat it as the key.
/// </summary>
public sealed record EnrollmentKeyCreated(EnrollmentKeySummary Key, string Plaintext);

/// <summary>One line of a key's audit trail: what was attempted with it, and what became of it.</summary>
public sealed record EnrollmentKeyEvent(
    string Id,
    string? DeviceId,
    string? DeviceName,
    string Source,
    string Outcome,
    string Description,
    DateTimeOffset CreatedAt);

/// <summary>One administrator account. The password hash is never part of this.</summary>
public sealed record AdminAccount(
    string Id,
    string Username,
    bool Disabled,
    string Source,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public sealed record AdminAccountCreate(string Username, string Password);

public sealed record AdminPasswordReset(string Password);

/// <summary>
/// The settings the whole server shares, as the settings page edits them. Not to be confused with
/// <see cref="PortalSettings"/>, which is the client's own configuration file on a PC.
/// </summary>
public sealed record AdminSettings(string DefaultEngine);

/// <summary>The tiles on the web dashboard, as numbers.</summary>
public sealed record DashboardCounts(
    int Devices,
    int InstallsToday,
    int FailuresThisWeek,
    int ActiveNow,
    int PendingRequests);

/// <summary>
/// One session an administrator holds. <see cref="Current"/> marks the one that made this very call,
/// so a client can show "this device" and avoid signing itself out by accident.
/// </summary>
public sealed record AdminSessionSummary(
    string Id,
    string? DeviceName,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? LastUsedAt,
    bool Current);

public static class AdminApiRoutes
{
    /// <summary>Admin JSON routes, bearer <c>apa_</c> tokens, the <c>Admin</c> policy on every one.</summary>
    public const string Prefix = "/api/v1/admin";

    public const string Session = Prefix + "/session";
    public const string Sessions = Prefix + "/sessions";
    public const string Dashboard = Prefix + "/dashboard";
    public const string Installs = Prefix + "/installs";
    public const string Requests = Prefix + "/requests";
    public const string Catalog = Prefix + "/catalog";
    public const string Devices = Prefix + "/devices";
    public const string Keys = Prefix + "/keys";
    public const string Admins = Prefix + "/admins";
    public const string Settings = Prefix + "/settings";
}

public static class AdminApiLimits
{
    /// <summary>Rows a list returns when the caller does not say.</summary>
    public const int DefaultLimit = 50;

    /// <summary>
    /// The most rows one call may return, whatever it asks for. A fleet's whole install history in one
    /// response is a way to take the server down with a single request that passed authentication.
    /// </summary>
    public const int MaxLimit = 200;

    /// <summary>
    /// The largest catalog file an import will read, in bytes as sent, so a body cannot exhaust memory.
    /// Bytes and not characters: a character can take up to four bytes in UTF-8.
    /// </summary>
    public const int MaxImportBytes = 4 * 1024 * 1024;
}
