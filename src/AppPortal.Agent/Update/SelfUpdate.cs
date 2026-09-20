using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Agent.Jobs;
using AppPortal.Shared;

namespace AppPortal.Agent.Update;

/// <summary>
/// One pass of the self-update: what is published, what is installed, and what to do about the
/// difference. Everything outside the state folder is reached through an interface, so the whole
/// decision is under test without a network, a release or Windows Installer.
/// </summary>
public sealed class SelfUpdate(
    IReleaseFeed feed,
    IUpdateDownloader downloads,
    IProcessRunner processes,
    IClientPresence client,
    UpdatePaths paths,
    ILogger<SelfUpdate> logger,
    Func<Version?>? installedVersion = null,
    TimeSpan? timeout = null)
{
    private const int RebootRequired = 3010;

    private const int RebootInitiated = 1641;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Func<Version?> _installed = installedVersion ?? AgentVersion;

    // Windows Installer on a slow machine with a large MSI is measured in minutes, not seconds, and
    // giving up early would leave a half-finished install nobody asked for.
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromMinutes(30);

    /// <summary>Runs a pass and writes what happened to update.json, whatever happened.</summary>
    public async Task<UpdateStatus> RunAsync(CancellationToken ct)
    {
        UpdateStatus status;
        try
        {
            status = await CheckAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("The update pass failed unexpectedly ({Reason}: {Message})", ex.GetType().Name, ex.Message);
            status = Failed(_installed(), null, ex.Message);
        }

        Write(status);
        return status;
    }

    /// <summary>The arguments Windows Installer gets. A silent install that never restarts a PC on its own.</summary>
    internal static string MsiexecArguments(string msi, string log)
        => $"/i \"{msi}\" /qn /norestart /l*v \"{log}\"";

    private static Version? AgentVersion()
        => VersionText.TryParse(typeof(SelfUpdate).Assembly.GetName().Version?.ToString(), out var version) ? version : null;

    private async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        var installed = _installed();
        if (installed is null)
        {
            return Failed(null, null, "The installed version could not be read.");
        }

        var installedText = VersionText.Short(installed);
        ReleaseInfo? latest;
        try
        {
            latest = await feed.GetLatestAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation("The release feed could not be reached ({Reason})", ex.GetType().Name);
            return new UpdateStatus(DateTimeOffset.UtcNow, installedText, null, null, UpdateResult.Offline, "The update service could not be reached.");
        }
        catch (InvalidDataException ex)
        {
            // Reached, answered, and the answer was unusable: a release with no MSI for the version it
            // names, a tag that is not a version, an empty body. Saying "could not be reached" sends
            // whoever reads it to the network, and the fault is in the release. This is the shape a
            // renamed or missing asset takes, and it is the one a whole fleet meets on the same
            // afternoon, so it says what is wrong and shows as a failure rather than as weather.
            logger.LogError("The newest release cannot be used ({Message})", ex.Message);
            return Failed(installed, null, ex.Message);
        }

        if (latest is null || latest.Version <= installed)
        {
            Prune(null);
            return new UpdateStatus(DateTimeOffset.UtcNow, installedText,
                latest is null ? installedText : VersionText.Short(latest.Version), null, UpdateResult.UpToDate, null);
        }

        var latestText = VersionText.Short(latest.Version);
        string msi;
        try
        {
            msi = await downloads.FetchAsync(latest, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException
                                       or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("{Version} could not be downloaded ({Message})", latestText, ex.Message);
            return Failed(installed, latestText, ex.Message);
        }

        Prune(latest.Version);
        if (client.IsRunning())
        {
            // The MSI replaces files the running client holds open. StagedVersion is what the client's
            // "Restart to update" banner reads, and pressing it leaves a request and closes the window.
            return new UpdateStatus(DateTimeOffset.UtcNow, installedText, latestText, latestText,
                UpdateResult.Available, "Close App Portal to finish updating.");
        }

        return await ApplyAsync(latest, msi, installed);
    }

    private async Task<UpdateStatus> ApplyAsync(ReleaseInfo latest, string msi, Version installed)
    {
        var installedText = VersionText.Short(installed);
        var latestText = VersionText.Short(latest.Version);
        var log = paths.LogPath(latest.Version);
        logger.LogInformation("Installing {Version} over {Installed}", latestText, installedText);

        // The MSI stops this service on its way through, so this pass may be killed before it can
        // report anything. Saying what is starting means the client has something true to show in the
        // meantime; the first pass after the service comes back writes the outcome.
        Write(new UpdateStatus(DateTimeOffset.UtcNow, installedText, latestText, latestText,
            UpdateResult.Available, $"Installing {latestText}."));

        ProcessResult result;
        try
        {
            // Not the stopping token. Cancelling here would kill Windows Installer part way through
            // replacing files, and the service being stopped is how this upgrade is supposed to go.
            result = await processes.RunAsync("msiexec.exe", MsiexecArguments(msi, log), null, _timeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogError("Windows Installer could not be run ({Message})", ex.Message);
            return Failed(installed, latestText, ex.Message);
        }

        if (result.ExitCode is 0 or RebootRequired or RebootInitiated)
        {
            Prune(null);
            logger.LogInformation("Installed {Version}", latestText);
            return new UpdateStatus(DateTimeOffset.UtcNow, latestText, latestText, null, UpdateResult.Installed,
                result.ExitCode == 0 ? $"Updated to {latestText}." : $"Updated to {latestText}. This PC has to restart to finish.");
        }

        // The MSI stays where it is along with its log: a failure that repeats every day is worth
        // reading, and re-downloading it to fail the same way is not.
        logger.LogError("Windows Installer exited {Code} installing {Version}", result.ExitCode, latestText);
        return Failed(installed, latestText, $"Windows Installer exited {result.ExitCode}. The log is at {log}.");
    }

    private UpdateStatus Failed(Version? installed, string? latest, string message)
        => new(DateTimeOffset.UtcNow, installed is null ? "unknown" : VersionText.Short(installed), latest, null, UpdateResult.Failed, message);

    /// <summary>
    /// Throws away every downloaded MSI but the one named, which after a successful install is none of
    /// them. Ten megabytes a release adds up on a machine nobody looks at.
    /// </summary>
    private void Prune(Version? keep)
    {
        if (!Directory.Exists(paths.UpdatesDir))
        {
            return;
        }

        var wanted = keep is null ? null : GitHubReleaseFeed.MsiAssetName(keep);
        foreach (var file in Directory.EnumerateFiles(paths.UpdatesDir, "*.msi"))
        {
            if (wanted is not null && string.Equals(Path.GetFileName(file), wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogInformation("{File} stays for now ({Reason})", Path.GetFileName(file), ex.GetType().Name);
            }
        }
    }

    private void Write(UpdateStatus status)
    {
        try
        {
            Directory.CreateDirectory(paths.StateDir);
            // Readers should see one whole result even if they open the file while it is replaced.
            var temp = paths.StatusPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status, Json));
            File.Move(temp, paths.StatusPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not write the update status ({Reason})", ex.GetType().Name);
        }
    }
}
