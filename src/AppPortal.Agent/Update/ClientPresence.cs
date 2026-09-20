using System.Diagnostics;

namespace AppPortal.Agent.Update;

/// <summary>
/// Whether somebody has the client open. Windows Installer cannot replace a file that is held open,
/// so this is what decides between installing now and waiting for the person to close it.
/// </summary>
public interface IClientPresence
{
    bool IsRunning();
}

/// <summary>
/// A client from this install folder counts; a build somewhere else, such as a developer's, does not.
/// When a process cannot be inspected, assume it is ours: waiting a day longer costs nothing, and
/// upgrading underneath a running client costs a failed install.
/// </summary>
public sealed class InstalledClientPresence(string installDir) : IClientPresence
{
    public bool IsRunning()
    {
        foreach (var process in Process.GetProcessesByName("AppPortal"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || path.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
