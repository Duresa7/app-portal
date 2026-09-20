using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace AppPortal.Setup.Services;

/// <summary>
/// The real implementations wired together. Kept in one place so a test can hand the same view model
/// fakes and walk the whole wizard without an MSI, a server, or Windows.
/// </summary>
public static class Composition
{
    public static SetupRun Run(IEnrollmentProbe probe)
        => new(
            new EmbeddedMsiSource(),
            probe,
            new MsiInstall(new ProcessRunner()),
            new EnrollmentWatcher(EnrollmentWatcher.DefaultDirectory),
            () => DateTimeOffset.UtcNow,
            SetupRun.DefaultScratchDirectory);

    /// <summary>Starts the installed client. False when it is not where the MSI puts it.</summary>
    public static bool Launch(string path)
    {
        try
        {
            using var started = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return started is not null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            return false;
        }
    }
}
