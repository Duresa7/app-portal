namespace AppPortal.Server.Options;

/// <summary>
/// Optional directory sign-in for administrators. Off unless a deployment turns it on: the portal runs
/// with local accounts alone and has no dependency on any directory. When it is on, an administrator may
/// sign in with a domain account that belongs to one configured group, and nothing else changes.
/// </summary>
public sealed class DirectoryOptions
{
    public const string Section = "Directory";

    public bool Enabled { get; set; }

    /// <summary>Domain controllers to try in order. Either "host" or "host:port"; the port here wins.</summary>
    public string[] Servers { get; set; } = [];

    /// <summary>LDAPS. Plain 389 is not offered: a simple bind sends the password.</summary>
    public int Port { get; set; } = 636;

    /// <summary>
    /// The NetBIOS domain a bare user name is prefixed with, and the name the portal stores the account
    /// under, so an administrator row reads the same as the requester on an install: <c>DOMAIN\user</c>.
    /// </summary>
    public string NetBiosDomain { get; set; } = "";

    /// <summary>
    /// Appended to a bare user name when no NetBIOS domain is configured. A forest's UPN suffix often
    /// differs from its DNS name, so this is configured rather than derived.
    /// </summary>
    public string UpnSuffix { get; set; } = "";

    /// <summary>Search base. Empty means ask the controller for its default naming context.</summary>
    public string BaseDn { get; set; } = "";

    /// <summary>
    /// sAMAccountName or full DN of the group an account must be in, nested membership included. Required:
    /// without it every account in the directory would administer the portal.
    /// </summary>
    public string RequiredGroup { get; set; } = "";

    /// <summary>
    /// SHA-256 thumbprints, hex, of certificates the controllers may present. A forest with no certificate
    /// authority issues its controllers self-signed certificates that no chain can validate, and pinning is
    /// the honest answer to that. Empty means ordinary chain validation.
    /// </summary>
    public string[] CertificateThumbprints { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>Throws when the section is on but cannot work, so the server refuses at startup, not at sign-in.</summary>
    public void Validate()
    {
        if (!Enabled)
        {
            return;
        }

        if (Servers.Length == 0)
        {
            throw new InvalidOperationException($"{Section}:Enabled is true but no {Section}:Servers are configured.");
        }

        if (string.IsNullOrWhiteSpace(RequiredGroup))
        {
            throw new InvalidOperationException(
                $"{Section}:RequiredGroup is required when {Section}:Enabled is true. Name the group whose members administer the portal.");
        }

        if (string.IsNullOrWhiteSpace(NetBiosDomain) && string.IsNullOrWhiteSpace(UpnSuffix))
        {
            throw new InvalidOperationException(
                $"Set {Section}:NetBiosDomain or {Section}:UpnSuffix so a bare user name can be turned into a bind name.");
        }
    }
}
