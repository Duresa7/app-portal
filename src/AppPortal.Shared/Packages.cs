using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AppPortal.Shared;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(WingetPackageDefinition), "winget")]
[JsonDerivedType(typeof(DirectPackageDefinition), "direct")]
public abstract record PackageDefinition
{
    public abstract void Validate();
}

public sealed record WingetPackageDefinition(
    string Id,
    string Scope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ExtraArgs = null) : PackageDefinition
{
    public override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || !Regex.IsMatch(Id, @"\A[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_-]+)+\z"))
        {
            throw new InvalidDataException("A winget package needs an id such as Valve.Steam.");
        }

        if (Scope is not ("machine" or "user"))
        {
            throw new InvalidDataException("The winget scope must be machine or user.");
        }
    }
}

public sealed record DirectPackageDefinition(
    string Url,
    string Sha256,
    string InstallerType,
    string SilentArgs,
    long SizeBytes,
    string UninstallKey) : PackageDefinition
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
            throw new InvalidDataException("The installer type must be msi, exe or msix.");
        }

        if (string.IsNullOrWhiteSpace(SilentArgs))
        {
            throw new InvalidDataException("A direct installer needs silent arguments.");
        }

        if (SizeBytes <= 0)
        {
            throw new InvalidDataException("A direct installer needs a positive sizeBytes.");
        }

        if (string.IsNullOrWhiteSpace(UninstallKey))
        {
            throw new InvalidDataException("A direct installer needs an uninstall key.");
        }
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
