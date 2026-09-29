using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;

using Microsoft.Win32;

namespace AppPortal.Agent.Executors;

/// <summary>
/// One person's branch of HKEY_USERS. The agent runs as LocalSystem, so its own HKEY_CURRENT_USER is
/// the service account's and never theirs, and anything a per-user installer or launcher wrote lives in
/// theirs.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class UserHive
{
    /// <summary>
    /// The branch, or null when it is not there to read. Windows keeps a profile's hive loaded while
    /// that person is signed in, and everything that reads one runs while they are, so this is a read
    /// rather than a mount. Signed out, they have no hive and no answer.
    /// </summary>
    public static RegistryKey? Open(string account)
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
