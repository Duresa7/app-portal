using System.Runtime.Versioning;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Reading what Windows records about how to remove something. Behind an interface because the only
/// thing it does is touch the registry, and that is not a thing to need a Windows machine to test.
/// </summary>
public interface IUninstallRegistry
{
    /// <summary>
    /// The command that removes this app without asking anybody anything, or null when its own
    /// uninstall entry offers only an interactive one, which is a dead end for a service.
    /// </summary>
    string? QuietUninstallString(string uninstallKey);
}

/// <summary>What the agent uses away from Windows, and in tests: nothing is removable.</summary>
public sealed class NoUninstallRegistry : IUninstallRegistry
{
    public string? QuietUninstallString(string uninstallKey) => null;
}

[SupportedOSPlatform("windows")]
public sealed class WindowsUninstallRegistry : IUninstallRegistry
{
    /// <summary>
    /// Both views, because a 32-bit installer on a 64-bit PC writes under WOW6432Node and a 64-bit one
    /// does not, and an administrator filling in the catalog should not have to know which.
    /// </summary>
    private static readonly string[] Roots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\",
    ];

    public string? QuietUninstallString(string uninstallKey)
    {
        foreach (var root in Roots)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(root + uninstallKey);
                if (key?.GetValue("QuietUninstallString") is string quiet && !string.IsNullOrWhiteSpace(quiet))
                {
                    return quiet;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // An unreadable key is one that offers nothing, which is the same answer.
            }
        }

        return null;
    }
}
