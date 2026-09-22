using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

using Microsoft.Win32;

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
    /// <param name="uninstallKey">The key name under the Uninstall branch.</param>
    /// <param name="account">
    /// Whose copy to look for, as DOMAIN\user, or null for one installed for the whole machine. An
    /// application that installs into a profile writes its uninstall entry into that person's hive,
    /// and the agent runs as LocalSystem, so its own HKEY_CURRENT_USER is the service account's and
    /// never theirs.
    /// </param>
    string? QuietUninstallString(string uninstallKey, string? account = null);
}

/// <summary>What the agent uses away from Windows, and in tests: nothing is removable.</summary>
public sealed class NoUninstallRegistry : IUninstallRegistry
{
    public string? QuietUninstallString(string uninstallKey, string? account = null) => null;
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

    public string? QuietUninstallString(string uninstallKey, string? account = null)
    {
        // The person's own hive first. A per-user application writes there and nowhere else, which is
        // why a machine-wide lookup could never remove one. Falling through to the machine afterwards
        // costs one missed read and covers an installer that writes to both.
        if (account is { Length: > 0 } && Hive(account) is { } hive)
        {
            using (hive)
            {
                if (Read(hive, uninstallKey) is { } theirs)
                {
                    return theirs;
                }
            }
        }

        return Read(Registry.LocalMachine, uninstallKey);
    }

    private static string? Read(RegistryKey root, string uninstallKey)
    {
        foreach (var branch in Roots)
        {
            try
            {
                using var key = root.OpenSubKey(branch + uninstallKey);
                if (key?.GetValue("QuietUninstallString") is string quiet && !string.IsNullOrWhiteSpace(quiet))
                {
                    return quiet;
                }
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // An unreadable key is one that offers nothing, which is the same answer.
            }
        }

        return null;
    }

    /// <summary>
    /// This person's branch of HKEY_USERS, or null when it is not there to read. Windows keeps a
    /// profile's hive loaded while that person is signed in, and a per-user removal only runs while
    /// they are, so this is a read rather than a mount. Signed out, they have no hive and no answer.
    /// </summary>
    private static RegistryKey? Hive(string account)
    {
        try
        {
            var sid = (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
            return Registry.Users.OpenSubKey(sid.Value);
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or SecurityException
                                       or UnauthorizedAccessException or SystemException)
        {
            return null;
        }
    }
}
