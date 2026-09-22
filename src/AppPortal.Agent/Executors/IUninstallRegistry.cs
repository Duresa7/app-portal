using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

using Microsoft.Win32;

namespace AppPortal.Agent.Executors;

/// <summary>
/// What Windows records about how to remove something, and taking an entry out of that record. Behind
/// an interface because the only thing it does is touch the registry, and that is not a thing to need a
/// Windows machine to test.
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

    /// <summary>
    /// Takes one entry out of the list Windows shows in Apps and Features, and says whether there was
    /// one to take. Only the named key goes; every other entry, this product's own included, is left
    /// exactly as it was.
    /// </summary>
    bool Remove(string uninstallKey);
}

/// <summary>
/// What may be handed to a registry delete as the name of one uninstall entry. Its own type, and not a
/// private check inside the Windows implementation, because the rule is the interesting part and a rule
/// nobody can test on a build machine is a rule nobody checks.
/// </summary>
public static class UninstallKeyName
{
    /// <summary>
    /// Whether this names one entry and nothing wider. A registry delete takes a path, so a blank name
    /// names the Uninstall branch itself and would take every entry on the PC with it, and a name
    /// carrying a separator reaches somewhere nobody asked for.
    /// <para>
    /// An allowlist, not a list of characters to fear: these are what a real uninstall key is made of,
    /// a product code's braces and hyphens included.
    /// </para>
    /// </summary>
    public static bool NamesOneKey(string? uninstallKey)
        => !string.IsNullOrWhiteSpace(uninstallKey) && uninstallKey.All(Allowed);

    private static bool Allowed(char c)
        => char.IsAsciiLetterOrDigit(c) || c is '{' or '}' or '(' or ')' or '-' or '_' or '.' or '+' or '~' or ' ';
}

/// <summary>What the agent uses away from Windows, and in tests: nothing is removable.</summary>
public sealed class NoUninstallRegistry : IUninstallRegistry
{
    public string? QuietUninstallString(string uninstallKey, string? account = null) => null;

    public bool Remove(string uninstallKey) => false;
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

    public bool Remove(string uninstallKey)
    {
        if (!UninstallKeyName.NamesOneKey(uninstallKey))
        {
            return false;
        }

        var removed = false;
        foreach (var branch in Roots)
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(branch + uninstallKey))
                {
                    if (key is null)
                    {
                        continue;
                    }
                }

                Registry.LocalMachine.DeleteSubKeyTree(branch + uninstallKey, throwOnMissingSubKey: false);
                removed = true;
            }
            catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
            {
                // An entry that will not come away stays where it is, and the caller hears that nothing
                // was removed. Whatever wanted it gone has better things to do than fail over it.
            }
        }

        return removed;
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
