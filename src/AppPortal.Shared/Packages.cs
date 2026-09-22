using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AppPortal.Shared;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WingetPackageDefinition), "winget")]
[JsonDerivedType(typeof(DirectPackageDefinition), "direct")]
[JsonDerivedType(typeof(ManagedPackageDefinition), "managed")]
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
        ManagedPackageDefinition => "managed",
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

/// <summary>
/// A package winget can install, from either of the two sources it reads. The Microsoft Store is not a
/// separate install mechanism: it is winget's own <c>msstore</c> source, so the locator that finds
/// winget.exe for SYSTEM, the output parser, the exit-code map and the inventory sweep all apply to a
/// Store app unchanged.
/// <para>
/// <c>Source</c> is last rather than beside <c>Id</c> where it reads better. This is a positional
/// record with callers that pass id and scope positionally, and a new string parameter anywhere before
/// the end binds one of their strings to the wrong place, in silence, because the compiler has no way
/// to tell three strings apart.
/// </para>
/// </summary>
public sealed record WingetPackageDefinition(
    string Id,
    string Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExtraArgs = null,
    bool RequiresReboot = false,
    string Source = WingetSources.Winget) : PackageDefinition
{
    public override void Validate()
    {
        if (Source is not (WingetSources.Winget or WingetSources.Store))
        {
            throw new InvalidDataException($"The source must be {WingetSources.Winget} or {WingetSources.Store}.");
        }

        if (Source == WingetSources.Store)
        {
            // A Store product id is twelve characters with no publisher and no dot in it, so the rule
            // below would reject every Store app there is.
            if (string.IsNullOrWhiteSpace(Id) || !Regex.IsMatch(Id, @"\A[A-Za-z0-9]{12}\z"))
            {
                throw new InvalidDataException(
                    "A Microsoft Store package needs a twelve-character product id such as 9WZDNCRFJ3TJ, which is the last part of its address in the Store.");
            }
        }
        else if (string.IsNullOrWhiteSpace(Id) || !Regex.IsMatch(Id, @"\A[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+\z"))
        {
            throw new InvalidDataException("A winget package needs an id such as Valve.Steam.");
        }

        ValidateScope(Scope);
    }
}

/// <summary>
/// A package one of the PC's own package managers knows: Chocolatey, Scoop, npm, Yarn, Bun, pip,
/// Cargo, vcpkg, a .NET tool or a PowerShell module. One record and one executor for all of them,
/// because the only thing that differs between them is a command line, and a command line is data.
/// <see cref="PackageManagers"/> holds one row per manager.
/// </summary>
public sealed record ManagedPackageDefinition(
    string Manager,
    string Id,
    string Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExtraArgs = null,
    bool RequiresReboot = false) : PackageDefinition
{
    /// <summary>
    /// What a version may look like. An allowlist for the same reason the id has one: it reaches a
    /// command line, and several of these managers are batch files.
    /// </summary>
    private static readonly Regex VersionRule = new(@"\A[A-Za-z0-9][A-Za-z0-9.+_-]*\z");

    public override void Validate()
    {
        var manager = PackageManagers.Find(Manager)
                      ?? throw new InvalidDataException($"The package manager must be one of: {PackageManagers.Names}.");

        // The id reaches a command line, and npm, Yarn and Scoop are batch files, which Windows runs
        // through cmd.exe. An id carrying an ampersand or a pipe is a command rather than a package
        // name, so what is allowed is listed rather than what is not.
        if (string.IsNullOrWhiteSpace(Id) || !manager.IdRule.IsMatch(Id))
        {
            throw new InvalidDataException(
                $"'{Id}' is not a {manager.DisplayName} package id. Use letters, digits, and the separators {manager.DisplayName} itself uses.");
        }

        if (Version is { Length: > 0 })
        {
            if (!manager.CanPinVersion)
            {
                throw new InvalidDataException(
                    $"{manager.DisplayName} installs the version its own source offers and cannot be asked for another. Leave the version empty.");
            }

            if (!VersionRule.IsMatch(Version))
            {
                throw new InvalidDataException($"'{Version}' is not a version. Use letters, digits, dots and hyphens.");
            }
        }

        ValidateScope(Scope);
        if (!manager.Scopes.Contains(Scope))
        {
            var only = string.Join(" or ", manager.Scopes);
            throw new InvalidDataException($"{manager.DisplayName} can only install at {only} scope on Windows.");
        }
    }
}

/// <summary>The two sources winget reads, named once so nothing spells them twice.</summary>
public static class WingetSources
{
    public const string Winget = "winget";

    public const string Store = "msstore";

    /// <summary>
    /// What a Store package installs into. Every one of them is an MSIX and lands in a profile, so
    /// machine scope is refusable by winget rather than by us, and the executor's existing message
    /// explains a refusal without a code path of its own.
    /// </summary>
    public const string StoreScope = "user";
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
