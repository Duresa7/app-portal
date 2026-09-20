using AppPortal.Shared;

namespace AppPortal.Agent.Update;

/// <summary>
/// Where an update lives while it waits. The install folder is wherever the agent runs from, which on
/// a deployed machine is %ProgramFiles%\App Portal; everything else sits in %ProgramData%\AppPortal,
/// which the installer made readable by every user. The downloaded MSI and its Windows Installer log
/// go under <c>updates</c>, the status the client reads is <c>update.json</c>, and the one thing a
/// signed-in user may leave behind is <c>update.request</c>.
/// </summary>
public sealed class UpdatePaths
{
    public UpdatePaths(string installDir, string stateDir)
    {
        InstallDir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar);
        StateDir = Path.GetFullPath(stateDir).TrimEnd(Path.DirectorySeparatorChar);
    }

    public string InstallDir { get; }

    public string StateDir { get; }

    public string UpdatesDir => Path.Combine(StateDir, "updates");

    public string StatusPath => Path.Combine(StateDir, "update.json");

    public string RequestPath => Path.Combine(StateDir, "update.request");

    public string MsiPath(string assetName) => Path.Combine(UpdatesDir, assetName);

    /// <summary>One verbose log per version, kept after the install so a failure can be read afterwards.</summary>
    public string LogPath(Version version) => Path.Combine(UpdatesDir, $"update-{VersionText.Short(version)}.log");
}
