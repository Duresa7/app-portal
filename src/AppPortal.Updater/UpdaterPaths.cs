namespace AppPortal.Updater;

/// <summary>
/// Where everything lives. The install folder is wherever this executable runs from, which on a
/// deployed machine is %ProgramFiles%\App Portal. Scratch space stays inside it, so only an
/// administrator can touch a download or a staged build; the status file and log go to
/// %ProgramData%\AppPortal, where the installer made them readable by every user.
/// </summary>
public sealed class UpdaterPaths
{
    public static readonly string[] Reserved = [".update", ".staged", ".previous"];

    public UpdaterPaths(string installDir, string stateDir)
    {
        InstallDir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar);
        StateDir = Path.GetFullPath(stateDir).TrimEnd(Path.DirectorySeparatorChar);
    }

    public static UpdaterPaths Default()
    {
        var state = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppPortal")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "AppPortal");
        return new UpdaterPaths(AppContext.BaseDirectory, state);
    }

    public string InstallDir { get; }
    public string StateDir { get; }

    /// <summary>Download and extraction scratch space.</summary>
    public string WorkDir => Path.Combine(InstallDir, ".update");

    /// <summary>A verified build waiting to be swapped in.</summary>
    public string StagedDir => Path.Combine(InstallDir, ".staged");

    /// <summary>The build that was just replaced. Deleted on the next run, once nothing holds it open.</summary>
    public string PreviousDir => Path.Combine(InstallDir, ".previous");

    public string ClientExe => Path.Combine(InstallDir, "AppPortal.exe");
    public string StatusPath => Path.Combine(StateDir, "update.json");
    public string LogPath => Path.Combine(StateDir, "updater.log");
    public string SettingsPath => Path.Combine(StateDir, "client.json");
}
