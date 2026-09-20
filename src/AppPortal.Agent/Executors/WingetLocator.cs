namespace AppPortal.Agent.Executors;

/// <summary>
/// Finding winget.exe from a service account. It is not on the PATH for SYSTEM: the shim that puts it
/// there for a signed-in person lives under that person's own WindowsApps folder. The real executable
/// sits in the package directory under Program Files, whose name carries the version, so the newest
/// one wins and the search is a directory listing rather than a guess.
/// </summary>
public sealed class WingetLocator(string? windowsAppsRoot = null)
{
    private const string PackagePrefix = "Microsoft.DesktopAppInstaller_";

    private const string PackageSuffix = "__8wekyb3d8bbwe";

    private readonly string _root = windowsAppsRoot
                                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

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
