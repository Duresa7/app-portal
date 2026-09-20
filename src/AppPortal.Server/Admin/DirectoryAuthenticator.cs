using System.DirectoryServices.Protocols;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using AppPortal.Server.Options;

using Microsoft.Extensions.Options;

namespace AppPortal.Server.Admin;

/// <summary>What the directory said about one sign-in attempt.</summary>
public enum DirectoryOutcome
{
    /// <summary>The feature is off, so nothing was asked.</summary>
    Disabled,

    /// <summary>The controller refused the user name and password.</summary>
    BadCredentials,

    /// <summary>The password was right and the account is not in the group that administers the portal.</summary>
    NotInGroup,

    /// <summary>No controller could be reached, or one answered something unusable.</summary>
    Unavailable,

    Success,
}

/// <summary>An account as the directory describes it, which is what the portal stores and displays.</summary>
public sealed record DirectoryUser(string Account, string Domain, string DisplayName, string DistinguishedName)
{
    /// <summary>The portal's user name for this account, matching the requester label on installs.</summary>
    public string Username => string.IsNullOrEmpty(Domain) ? Account : $"{Domain}\\{Account}";
}

public sealed record DirectoryResult(DirectoryOutcome Outcome, DirectoryUser? User = null, string? Detail = null)
{
    public static readonly DirectoryResult Off = new(DirectoryOutcome.Disabled);
}

public interface IDirectoryAuthenticator
{
    bool Enabled { get; }

    DirectoryResult Authenticate(string username, string password);
}

/// <summary>
/// Turns what somebody typed into a name a domain controller will bind. Down-level <c>DOMAIN\user</c> is
/// preferred because Active Directory always accepts it, while a UPN suffix is free to differ from the
/// forest's DNS name and often does.
/// </summary>
public static class DirectoryName
{
    public static (string BindName, string Domain, string Account) Resolve(string typed, DirectoryOptions options)
    {
        var input = (typed ?? "").Trim();
        var slash = input.IndexOf('\\');
        if (slash > 0)
        {
            var domain = input[..slash];
            var account = input[(slash + 1)..];
            return (input, domain.ToUpperInvariant(), account);
        }

        var at = input.IndexOf('@');
        if (at > 0)
        {
            var account = input[..at];
            return (input, options.NetBiosDomain.ToUpperInvariant(), account);
        }

        if (!string.IsNullOrWhiteSpace(options.NetBiosDomain))
        {
            var domain = options.NetBiosDomain.ToUpperInvariant();
            return ($"{domain}\\{input}", domain, input);
        }

        return ($"{input}@{options.UpnSuffix}", "", input);
    }

    /// <summary>RFC 4515 escaping, so a user name cannot rewrite the filter it is placed in.</summary>
    public static string Escape(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\5c"); break;
                case '*': builder.Append("\\2a"); break;
                case '(': builder.Append("\\28"); break;
                case ')': builder.Append("\\29"); break;
                case '\0': builder.Append("\\00"); break;
                case '/': builder.Append("\\2f"); break;
                default: builder.Append(c); break;
            }
        }

        return builder.ToString();
    }
}

/// <summary>
/// Binds against the configured controllers over LDAPS and checks one group. Nothing here writes to the
/// directory, and the portal never holds a service credential: the bind is the user's own.
/// </summary>
public sealed class LdapDirectoryAuthenticator(IOptions<DirectoryOptions> options, ILogger<LdapDirectoryAuthenticator> logger)
    : IDirectoryAuthenticator
{
    /// <summary>LDAP_MATCHING_RULE_IN_CHAIN: membership through nested groups counts.</summary>
    private const string InChain = ":1.2.840.113556.1.4.1941:";

    private readonly DirectoryOptions _options = options.Value;

    public bool Enabled => _options.Enabled;

    public DirectoryResult Authenticate(string username, string password)
    {
        if (!_options.Enabled)
        {
            return DirectoryResult.Off;
        }

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
        {
            return new DirectoryResult(DirectoryOutcome.BadCredentials);
        }

        var (bindName, domain, account) = DirectoryName.Resolve(username, _options);
        DirectoryResult? lastFailure = null;

        foreach (var server in _options.Servers)
        {
            try
            {
                return AskOne(server, bindName, password, domain, account);
            }
            catch (LdapException ex) when (ex.ErrorCode == (int)LdapError.InvalidCredentials)
            {
                // Authoritative: every controller in a forest answers this the same way.
                return new DirectoryResult(DirectoryOutcome.BadCredentials);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Directory sign-in through {Server} failed", server);
                lastFailure = new DirectoryResult(DirectoryOutcome.Unavailable, Detail: ex.Message);
            }
        }

        return lastFailure ?? new DirectoryResult(DirectoryOutcome.Unavailable, Detail: "No directory servers are configured.");
    }

    private DirectoryResult AskOne(string server, string bindName, string password, string domain, string account)
    {
        var (host, port) = Split(server, _options.Port);
        using var connection = new LdapConnection(new LdapDirectoryIdentifier(host, port))
        {
            AuthType = AuthType.Basic,
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = true;

        if (_options.CertificateThumbprints.Length > 0)
        {
            // Checked before the password is sent, and on every platform: the LDAP library's own callback
            // is Windows-only, and OpenLDAP validates against a CA file rather than a fingerprint.
            Pin(host, port);
        }

        if (OperatingSystem.IsWindows())
        {
            connection.SessionOptions.VerifyServerCertificate = (_, certificate) => Accept(certificate, host);
        }

        connection.Bind(new NetworkCredential(bindName, password));

        var baseDn = string.IsNullOrWhiteSpace(_options.BaseDn) ? DefaultNamingContext(connection) : _options.BaseDn;
        var user = FindUser(connection, baseDn, bindName, account)
                   ?? throw new InvalidOperationException($"The bind as '{bindName}' succeeded but the account could not be read back.");

        var groupDn = ResolveGroup(connection, baseDn)
                      ?? throw new InvalidOperationException($"The group '{_options.RequiredGroup}' does not exist in {baseDn}.");

        if (!InGroup(connection, user.DistinguishedName, groupDn))
        {
            return new DirectoryResult(DirectoryOutcome.NotInGroup, Detail: $"Not a member of {_options.RequiredGroup}.");
        }

        var resolved = user with { Domain = string.IsNullOrEmpty(domain) ? _options.NetBiosDomain.ToUpperInvariant() : domain };
        return new DirectoryResult(DirectoryOutcome.Success, resolved);
    }

    /// <summary>
    /// Opens the TLS connection first and compares what the controller presents with the pinned
    /// thumbprints, so a substituted certificate is refused before any password is sent. The bind that
    /// follows makes its own connection; this one exists to fail early and loudly.
    /// </summary>
    private void Pin(string host, int port)
    {
        using var client = new TcpClient();
        if (!client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(_options.TimeoutSeconds)))
        {
            throw new InvalidOperationException($"{host}:{port} did not accept a connection within the timeout.");
        }

        X509Certificate? presented = null;
        using var stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) =>
        {
            presented = certificate;
            return true;
        });

        stream.AuthenticateAsClient(host);
        if (presented is null)
        {
            throw new InvalidOperationException($"{host}:{port} completed a TLS handshake without presenting a certificate.");
        }

        var thumbprint = Convert.ToHexStringLower(SHA256.HashData(presented.GetRawCertData()));
        if (!_options.CertificateThumbprints.Any(t => Normalise(t) == thumbprint))
        {
            throw new InvalidOperationException(
                $"{host} presented a certificate with SHA-256 {thumbprint}, which is not in Directory:CertificateThumbprints.");
        }
    }

    private bool Accept(X509Certificate certificate, string host)
    {
        if (_options.CertificateThumbprints.Length > 0)
        {
            var presented = Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
            var pinned = _options.CertificateThumbprints.Any(t => Normalise(t) == presented);
            if (!pinned)
            {
                logger.LogWarning("{Host} presented a certificate with SHA-256 {Thumbprint}, which is not pinned", host, presented);
            }

            return pinned;
        }

        using var chain = new X509Chain();
        var built = chain.Build(new X509Certificate2(certificate));
        if (!built)
        {
            logger.LogWarning("{Host} presented a certificate that does not chain to a trusted root, and no thumbprint is pinned", host);
        }

        return built;
    }

    private static string Normalise(string thumbprint)
        => thumbprint.Replace(":", "").Replace(" ", "").Trim().ToLowerInvariant();

    private static (string Host, int Port) Split(string server, int fallback)
    {
        var colon = server.LastIndexOf(':');
        if (colon > 0 && int.TryParse(server[(colon + 1)..], out var port))
        {
            return (server[..colon], port);
        }

        return (server, fallback);
    }

    private static string DefaultNamingContext(LdapConnection connection)
    {
        var request = new SearchRequest("", "(objectClass=*)", SearchScope.Base, "defaultNamingContext");
        var response = (SearchResponse)connection.SendRequest(request);
        var value = First(response, "defaultNamingContext");
        return value ?? throw new InvalidOperationException("The controller did not publish a defaultNamingContext.");
    }

    private static DirectoryUser? FindUser(LdapConnection connection, string baseDn, string bindName, string account)
    {
        var escaped = DirectoryName.Escape(account);
        var upn = bindName.Contains('@') ? $"(userPrincipalName={DirectoryName.Escape(bindName)})" : "";
        var filter = $"(&(objectCategory=person)(objectClass=user)(|(sAMAccountName={escaped}){upn}))";
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, "sAMAccountName", "displayName", "distinguishedName");
        var response = (SearchResponse)connection.SendRequest(request);
        if (response.Entries.Count == 0)
        {
            return null;
        }

        var entry = response.Entries[0];
        var sam = Value(entry, "sAMAccountName") ?? account;
        var display = Value(entry, "displayName") ?? sam;
        return new DirectoryUser(sam, "", display, entry.DistinguishedName);
    }

    private string? ResolveGroup(LdapConnection connection, string baseDn)
    {
        if (_options.RequiredGroup.Contains('='))
        {
            return _options.RequiredGroup;
        }

        var filter = $"(&(objectClass=group)(sAMAccountName={DirectoryName.Escape(_options.RequiredGroup)}))";
        var request = new SearchRequest(baseDn, filter, SearchScope.Subtree, "distinguishedName");
        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count == 0 ? null : response.Entries[0].DistinguishedName;
    }

    private static bool InGroup(LdapConnection connection, string userDn, string groupDn)
    {
        var filter = $"(memberOf{InChain}={DirectoryName.Escape(groupDn)})";
        var request = new SearchRequest(userDn, filter, SearchScope.Base, "distinguishedName");
        var response = (SearchResponse)connection.SendRequest(request);
        return response.Entries.Count > 0;
    }

    private static string? First(SearchResponse response, string attribute)
        => response.Entries.Count == 0 ? null : Value(response.Entries[0], attribute);

    private static string? Value(SearchResultEntry entry, string attribute)
    {
        if (!entry.Attributes.Contains(attribute))
        {
            return null;
        }

        var values = entry.Attributes[attribute].GetValues(typeof(string));
        return values.Length == 0 ? null : (string)values[0];
    }

    private enum LdapError
    {
        InvalidCredentials = 49,
    }
}

/// <summary>Stands in when the section is off, so nothing downstream has to ask whether it is.</summary>
public sealed class DisabledDirectoryAuthenticator : IDirectoryAuthenticator
{
    public bool Enabled => false;

    public DirectoryResult Authenticate(string username, string password) => DirectoryResult.Off;
}
