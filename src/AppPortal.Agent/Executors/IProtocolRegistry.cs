using System.Runtime.Versioning;
using System.Security;

using Microsoft.Win32;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Whether a URI scheme has something to open it for one person. Asked before a game is handed to its
/// launcher: without a handler Windows answers the link with "Get an app to open this link", which on
/// somebody's desktop is a dialog about a thing they never asked for.
/// </summary>
public interface IProtocolRegistry
{
    /// <param name="scheme">The scheme alone, such as <c>steam</c>.</param>
    /// <param name="account">Whose registration to look for, as DOMAIN\user.</param>
    bool IsRegistered(string scheme, string account);
}

/// <summary>What the agent uses away from Windows: nothing is registered, so nothing is handed off.</summary>
public sealed class NoProtocolRegistry : IProtocolRegistry
{
    public bool IsRegistered(string scheme, string account) => false;
}

/// <summary>
/// The person's own classes first, then the machine's, which is the order Windows itself looks in. A
/// launcher installed for everyone registers under HKLM; one a person installed into their own profile
/// registers under their HKCU. Either one opens the link.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProtocolRegistry : IProtocolRegistry
{
    public bool IsRegistered(string scheme, string account)
    {
        if (string.IsNullOrWhiteSpace(scheme) || scheme.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '+')))
        {
            return false;
        }

        if (UserHive.Open(account) is { } hive)
        {
            using (hive)
            {
                if (IsProtocol(hive, @"Software\Classes\" + scheme))
                {
                    return true;
                }
            }
        }

        return IsProtocol(Registry.LocalMachine, @"SOFTWARE\Classes\" + scheme);
    }

    /// <summary>A class key is a protocol only when it says so with a URL Protocol value, whatever it holds.</summary>
    private static bool IsProtocol(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.GetValue("URL Protocol") is not null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
