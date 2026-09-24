using System.Security.Principal;
using System.Xml;
using System.Xml.Linq;

using Microsoft.Win32;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Finding winget.exe from a service account. It is not on the PATH for SYSTEM: the shim that puts it
/// there for a signed-in person lives under that person's own WindowsApps folder. The real executable
/// sits in the package directory under Program Files, whose name carries the version, so the newest
/// one wins and the search is a directory listing rather than a guess.
/// </summary>
public sealed class WingetLocator(string? windowsAppsRoot = null, Func<string, string?>? profileOf = null)
{
    private const string PackagePrefix = "Microsoft.DesktopAppInstaller_";

    private const string PackageSuffix = "__8wekyb3d8bbwe";

    private readonly string _root = windowsAppsRoot
                                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    private readonly Func<string, string?> _profileOf = profileOf ?? ProfilePath;

    /// <summary>
    /// The account's own App Execution Alias for winget, or null when it has none. This, and not the
    /// package path, is what runs in a person's session. Started by the agent into that session with
    /// CreateProcessAsUser, the package path is refused with STATUS_ACCESS_DENIED even for an account
    /// the App Installer is registered to; the alias is how Windows means a person to start a packaged
    /// command line, and it starts it inside its package. The alias appears once the App Installer is
    /// registered to the account, which is some time after its first sign-in.
    /// </summary>
    public string? ForAccount(string account)
    {
        if (_profileOf(account) is not { Length: > 0 } profile)
        {
            return null;
        }

        var alias = Path.Combine(profile, "AppData", "Local", "Microsoft", "WindowsApps", "winget.exe");
        return File.Exists(alias) ? alias : null;
    }

    /// <summary>The newest winget.exe on this device, or null when the App Installer is not present.</summary>
    public string? Find()
    {
        if (!Directory.Exists(_root))
        {
            return null;
        }

        string[] candidates;
        try
        {
            candidates = Directory.GetDirectories(_root, PackagePrefix + "*" + PackageSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // WindowsApps denies everyone but SYSTEM and TrustedInstaller. A developer run sees nothing.
            return null;
        }

        return candidates
            .Select(directory => new { Path = Path.Combine(directory, "winget.exe"), Version = VersionOf(directory) })
            .Where(candidate => File.Exists(candidate.Path))
            .OrderByDescending(candidate => candidate.Version)
            .Select(candidate => candidate.Path)
            .FirstOrDefault();
    }

    /// <summary>
    /// The framework packages <paramref name="executable"/> loads its DLLs from, the newest installed copy
    /// of each, to put ahead of PATH when it runs. Started by its path from outside its package, winget
    /// gets none of them: Windows resolves a package's dependencies only for an account the App Installer
    /// is registered to, which SYSTEM never is and a new account is not until some time after its first
    /// sign-in. Without them winget.exe exits at once with STATUS_DLL_NOT_FOUND and prints nothing.
    /// The list comes from the package's own manifest, so it follows the App Installer as it changes.
    /// </summary>
    public IReadOnlyList<string> Dependencies(string executable)
    {
        try
        {
            var manifest = File.ReadAllText(Path.Combine(Path.GetDirectoryName(executable)!, "AppxManifest.xml"));
            return DependencyDirectories(manifest, Directory.GetDirectories(_root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing to add is the answer from before this existed, and winget may still start.
            return [];
        }
    }

    /// <summary>
    /// For each PackageDependency in the manifest, the newest directory under WindowsApps with that
    /// package's name and the manifest's processor architecture. A dependency that is not installed is
    /// left out rather than failing the rest.
    /// </summary>
    internal static IReadOnlyList<string> DependencyDirectories(string manifest, IReadOnlyList<string> packages)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(manifest);
        }
        catch (XmlException)
        {
            return [];
        }

        var architecture = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity")
            ?.Attribute("ProcessorArchitecture")?.Value;
        var found = new List<string>();
        foreach (var name in document.Descendants().Where(e => e.Name.LocalName == "PackageDependency")
                     .Select(e => e.Attribute("Name")?.Value).OfType<string>().Where(n => n.Length > 0))
        {
            var newest = packages
                .Where(directory => IsPackage(Path.GetFileName(directory), name, architecture))
                .OrderByDescending(VersionOf)
                .FirstOrDefault();
            if (newest is not null)
            {
                found.Add(newest);
            }
        }

        return found;
    }

    /// <summary>
    /// Whether a folder named Name_Version_Architecture_ResourceId_PublisherId is this package. The name
    /// is compared whole, so Microsoft.VCLibs.140.00 does not also take Microsoft.VCLibs.140.00.UWPDesktop.
    /// </summary>
    private static bool IsPackage(string folder, string name, string? architecture)
    {
        var parts = folder.Split('_');
        return parts.Length == 5
               && string.Equals(parts[0], name, StringComparison.OrdinalIgnoreCase)
               && (architecture is null
                   || string.Equals(parts[2], architecture, StringComparison.OrdinalIgnoreCase)
                   || string.Equals(parts[2], "neutral", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What to start in this account's session, and what to put ahead of PATH for it: their alias with
    /// nothing added, since the alias starts winget inside its package and the package brings its own
    /// frameworks; or, while they have no alias yet, the package path with the framework folders.
    /// </summary>
    public (string Executable, IReadOnlyList<string> PathFirst) ForSession(string account, string executable)
        => ForAccount(account) is { } alias ? (alias, []) : (executable, Dependencies(executable));

    /// <summary>The profile folder Windows keeps for this account, or null when it has none here.</summary>
    private static string? ProfilePath(string account)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            var sid = ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
            using var profile = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
            return profile?.GetValue("ProfileImagePath") is string path && path.Length > 0
                ? Environment.ExpandEnvironmentVariables(path)
                : null;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or System.Security.SecurityException
                                       or UnauthorizedAccessException or IOException or SystemException)
        {
            return null;
        }
    }

    /// <summary>
    /// The version out of a name like Microsoft.DesktopAppInstaller_1.22.11141.0_x64__8wekyb3d8bbwe.
    /// A name that does not parse sorts last rather than throwing, because one odd directory in
    /// WindowsApps must not stop the others being considered.
    /// </summary>
    internal static Version VersionOf(string directory)
    {
        var name = Path.GetFileName(directory);
        var parts = name.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var version) ? version : new Version(0, 0);
    }
}
