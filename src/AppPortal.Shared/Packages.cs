using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AppPortal.Shared;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WingetPackageDefinition), "winget")]
[JsonDerivedType(typeof(DirectPackageDefinition), "direct")]
public abstract record PackageDefinition
{
    public abstract void Validate();

    /// <summary>
    /// The discriminator the JSON above writes, readable from code. The agent keys its executors on it,
    /// and it is ignored on the way out because the polymorphic writer already emits it.
    /// </summary>
    [JsonIgnore]
    public string Kind => this switch
    {
        WingetPackageDefinition => "winget",
        DirectPackageDefinition => "direct",
        _ => throw new InvalidOperationException($"{GetType().Name} has no kind; add it beside the JsonDerivedType attributes."),
    };

    /// <summary>
    /// Who the install is for. A machine-wide install runs as SYSTEM and serves everyone on the device.
    /// A per-user install runs in the requester's own session and lands in their profile, which is the
    /// only place the installers that write to %LocalAppData% can usefully go.
    /// </summary>
    public abstract string Scope { get; init; }

    /// <summary>Whether the software is only finished once the device restarts.</summary>
    public abstract bool RequiresReboot { get; init; }

    private protected static void ValidateScope(string scope)
    {
        if (scope is not ("machine" or "user"))
        {
            throw new InvalidDataException("The scope must be machine or user.");
        }
    }
}

public sealed record WingetPackageDefinition(
    string Id,
    string Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExtraArgs = null,
    bool RequiresReboot = false) : PackageDefinition
{
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || !Regex.IsMatch(Id, @"\A[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+\z"))
        {
            throw new InvalidDataException("A winget package needs an id such as Valve.Steam.");
        }

        ValidateScope(Scope);
    }
}

public sealed record DirectPackageDefinition(
    string Url,
    string Sha256,
    string InstallerType,
    string SilentArgs,
    long SizeBytes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UninstallKey = null,
    string Scope = "machine",
    bool RequiresReboot = false) : PackageDefinition
{
    public override void Validate()
    {
        ValidateUrl(Url);
        if (Sha256 is null || Sha256.Length != 64 || !Sha256.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("A direct installer needs a sha256 containing exactly 64 hexadecimal characters.");
        }

        if (InstallerType is not ("msi" or "exe" or "msix"))
        {
            // Naming only the three leaves somebody holding a Nullsoft or Inno Setup installer to
            // guess whether it is supported at all. It is: it is an exe.
            throw new InvalidDataException(
                "The installer type must be msi, exe or msix. Any other installer, such as Nullsoft or Inno Setup, is an exe.");
        }

        // An msix is installed by name through the packaging API and takes no command line at all, so
        // demanding arguments for one would only invite an administrator to invent some. The same is
        // true of an exe that is silent by default and publishes no switch: winget runs those with no
        // arguments, and the only way to say so here used to be to invent a switch, which is the very
        // thing the msix exemption exists to prevent.
        //
        // An msi is different and keeps the rule. The executor supplies /qn /norestart itself, so what
        // goes here is whatever else that particular package needs, and a blank field is far more
        // likely to be one nobody filled in.
        if (InstallerType is "msi" && string.IsNullOrWhiteSpace(SilentArgs))
        {
            throw new InvalidDataException("An msi installer needs silent arguments.");
        }

        if (SizeBytes <= 0)
        {
            throw new InvalidDataException("A direct installer needs a positive sizeBytes.");
        }

        // The uninstall key is a hint for reporting and removal, not something every installer has. An
        // msix is identified by its package family name, and the executor falls back to matching on the
        // app name when no key is given, so an administrator who does not know it may leave it out.
        ValidateScope(Scope);
    }

    public static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidDataException("A direct installer needs an absolute HTTP or HTTPS URL without credentials.");
        }

        return uri;
    }
}
