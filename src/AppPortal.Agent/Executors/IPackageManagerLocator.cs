using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.RegularExpressions;

using AppPortal.Shared;

using Microsoft.Win32;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Where a package manager's executable is on this PC, for the account that will run it. Behind an
/// interface because the answer comes from the file system and the registry, and the executor's own
/// behaviour has to be provable without either.
/// </summary>
public interface IPackageManagerLocator
{
    /// <summary>
    /// The full path to run, or null when the manager is not on this PC for that account. A full path
    /// and never a bare name: the session launcher hands the command line to CreateProcessAsUser with
    /// no application name, and the search that follows uses the agent's own PATH, which is SYSTEM's
    /// and knows nothing about a shim in somebody's profile.
    /// </summary>
    string? Find(PackageManagerDescriptor manager, string? account);
}

/// <summary>What the agent uses away from Windows, and in tests: no manager is anywhere.</summary>
public sealed class NoPackageManagerLocator : IPackageManagerLocator
{
    public string? Find(PackageManagerDescriptor manager, string? account) => null;
}

/// <summary>
/// The search itself, as a pure function of an environment and a way to ask whether a file exists.
/// The Windows class below only gathers those two inputs, so the part with any logic in it runs in an
/// ordinary unit test on any operating system.
/// </summary>
public static class PackageManagerSearch
{
    private static readonly Regex Variable = new(@"%([^%]+)%", RegexOptions.Compiled);

    public static string? Find(PackageManagerDescriptor manager, IReadOnlyDictionary<string, string> environment, Func<string, bool> fileExists)
    {
        foreach (var directory in manager.ProbeDirectories.Select(d => Expand(d, environment)).OfType<string>())
        {
            var candidate = Join(directory, manager.Executable);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        // The installers of these managers edit PATH, so PATH is the second place to look and not the
        // first: a probe directory is where the manager keeps itself, and PATH is where somebody may
        // have put an older copy.
        if (Lookup(environment, "PATH") is { } path)
        {
            foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var candidate = Join(directory, manager.Executable);
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>A directory with its %VARIABLES% filled in, or null when one of them has no value.</summary>
    private static string? Expand(string directory, IReadOnlyDictionary<string, string> environment)
    {
        var missing = false;
        var expanded = Variable.Replace(directory, match =>
        {
            var value = Lookup(environment, match.Groups[1].Value);
            missing |= value is null;
            return value ?? "";
        });
        return missing ? null : expanded;
    }

    private static string? Lookup(IReadOnlyDictionary<string, string> environment, string name)
        => environment.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    // Windows separators on purpose, whatever this test happens to run on. These paths are only ever
    // real on Windows, and Path.Combine on Linux would produce a mixture nothing recognises.
    private static string Join(string directory, string file)
        => directory.TrimEnd('\\') + '\\' + file;
}

[SupportedOSPlatform("windows")]
public sealed class WindowsPackageManagerLocator : IPackageManagerLocator
{
    public string? Find(PackageManagerDescriptor manager, string? account)
    {
        var environment = account is { Length: > 0 } ? ProfileEnvironment(account) : MachineEnvironment();
        return environment is null ? null : PackageManagerSearch.Find(manager, environment, File.Exists);
    }

    private static Dictionary<string, string> MachineEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    /// <summary>
    /// The agent's own environment with this person's profile written over it. The agent is SYSTEM, so
    /// its %USERPROFILE% is SYSTEM's and would send every per-profile probe to the wrong folder. The
    /// profile path comes from the same place Windows keeps it, and the person's own PATH additions
    /// from their hive, which is loaded while they are signed in, which is the only time this runs.
    /// </summary>
    private static Dictionary<string, string>? ProfileEnvironment(string account)
    {
        try
        {
            var sid = ((SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier))).Value;
            using var profile = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
            if (profile?.GetValue("ProfileImagePath") is not string path || path.Length == 0)
            {
                return null;
            }

            path = Environment.ExpandEnvironmentVariables(path);
            var environment = MachineEnvironment();
            environment["USERPROFILE"] = path;
            environment["LOCALAPPDATA"] = Path.Combine(path, "AppData", "Local");
            environment["APPDATA"] = Path.Combine(path, "AppData", "Roaming");

            using var hive = Registry.Users.OpenSubKey(sid + @"\Environment");
            if (hive?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string theirs && theirs.Length > 0)
            {
                var expanded = Variable.Replace(theirs, m => environment.GetValueOrDefault(m.Groups[1].Value, m.Value));
                environment["PATH"] = expanded + ";" + environment.GetValueOrDefault("PATH", "");
            }

            return environment;
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or System.Security.SecurityException
                                       or UnauthorizedAccessException or IOException or SystemException)
        {
            return null;
        }
    }

    private static readonly Regex Variable = new(@"%([^%]+)%", RegexOptions.Compiled);
}
