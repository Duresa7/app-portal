using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AppPortal.Shared;

namespace AppPortal.Updater;

/// <summary>
/// One run of the updater, normally started by the "App Portal Updater" scheduled task as SYSTEM:
/// at boot, at logon, once a day, and whenever the client asks. Pass --check to look without touching anything.
/// </summary>
public static partial class UpdateRun
{
    public const string DefaultRepository = "Duresa7/app-portal";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<int> MainAsync(string[] args)
    {
        var paths = UpdaterPaths.Default();
        Directory.CreateDirectory(paths.StateDir);
        var log = new UpdaterLog(paths.LogPath);

        using var single = new Mutex(initiallyOwned: false, OperatingSystem.IsWindows() ? @"Global\AppPortalUpdater" : "AppPortalUpdater");
        if (!single.WaitOne(TimeSpan.Zero))
        {
            log.Write("Another run is in progress; leaving it to finish.");
            return 0;
        }

        var checkOnly = args.Contains("--check", StringComparer.OrdinalIgnoreCase);
        var repository = ReadRepository(paths.SettingsPath) ?? DefaultRepository;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"AppPortal.Updater/{ThisVersion()}");

        var installer = new Installer(paths, log.Write);
        var feed = new ReleaseFeed(http, repository);
        log.Write($"Run started; repository {repository}{(checkOnly ? "; check only" : "")}.");

        UpdateStatus status;
        try
        {
            status = await RunAsync(installer, feed, http, checkOnly, log.Write, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.Write($"Unexpected failure: {ex}");
            status = new UpdateStatus(DateTimeOffset.UtcNow, VersionText.Short(installer.InstalledVersion() ?? new Version(0, 0, 0, 0)), null, null, UpdateResult.Failed, ex.Message);
        }

        WriteStatus(paths.StatusPath, status);
        log.Write($"Result {status.Result}{(status.Message is null ? "" : ": " + status.Message)}");
        return status.Result == UpdateResult.Failed ? 1 : 0;
    }

    public static async Task<UpdateStatus> RunAsync(Installer installer, ReleaseFeed feed, HttpClient http, bool checkOnly, Action<string> log, CancellationToken ct)
    {
        var installed = installer.InstalledVersion();
        if (installed is null)
        {
            return new UpdateStatus(DateTimeOffset.UtcNow, "unknown", null, null, UpdateResult.Failed, "AppPortal.exe was not found beside the updater.");
        }

        var installedText = VersionText.Short(installed);
        installer.CleanPrevious();

        // A build staged by an earlier run goes in first, if the client is closed now.
        var staged = installer.StagedVersion();
        if (staged is not null && staged > installed && !checkOnly)
        {
            if (await WaitForClientToCloseAsync(installer, TimeSpan.FromSeconds(20), ct))
            {
                installer.Apply();
                Record(staged);
                log($"Installed {VersionText.Short(staged)} over {installedText} from the staged copy.");
                return new UpdateStatus(DateTimeOffset.UtcNow, VersionText.Short(staged), VersionText.Short(staged), null, UpdateResult.Installed, $"Updated to {VersionText.Short(staged)}.");
            }

            return new UpdateStatus(DateTimeOffset.UtcNow, installedText, VersionText.Short(staged), VersionText.Short(staged), UpdateResult.Staged, "Close App Portal to finish updating.");
        }

        ReleaseInfo? latest;
        try
        {
            latest = await feed.GetLatestAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            log($"Release feed unavailable: {ex.Message}");
            return new UpdateStatus(DateTimeOffset.UtcNow, installedText, null, staged is null ? null : VersionText.Short(staged), UpdateResult.Offline, "The update service could not be reached.");
        }

        if (latest is null || latest.Version <= installed)
        {
            installer.DiscardStaged();
            return new UpdateStatus(DateTimeOffset.UtcNow, installedText, latest is null ? installedText : VersionText.Short(latest.Version), null, UpdateResult.UpToDate, null);
        }

        var latestText = VersionText.Short(latest.Version);
        if (checkOnly)
        {
            return new UpdateStatus(DateTimeOffset.UtcNow, installedText, latestText, staged is null ? null : VersionText.Short(staged), UpdateResult.Available, $"{latestText} is available.");
        }

        if (staged is null || staged != latest.Version)
        {
            try
            {
                var zip = await installer.DownloadAsync(http, latest, ct);
                installer.Stage(zip, latest.Version);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log($"Could not stage {latestText}: {ex.Message}");
                installer.DiscardStaged();
                return new UpdateStatus(DateTimeOffset.UtcNow, installedText, latestText, null, UpdateResult.Failed, ex.Message);
            }
        }

        if (await WaitForClientToCloseAsync(installer, TimeSpan.FromSeconds(20), ct))
        {
            installer.Apply();
            Record(latest.Version);
            log($"Installed {latestText} over {installedText}.");
            return new UpdateStatus(DateTimeOffset.UtcNow, latestText, latestText, null, UpdateResult.Installed, $"Updated to {latestText}.");
        }

        return new UpdateStatus(DateTimeOffset.UtcNow, installedText, latestText, latestText, UpdateResult.Staged, "Close App Portal to finish updating.");
    }

    /// <summary>
    /// The client asks for an update and then exits, so a short wait catches that hand-off. A client
    /// that is simply open stays open; the swap waits for the next run.
    /// </summary>
    private static async Task<bool> WaitForClientToCloseAsync(Installer installer, TimeSpan limit, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + limit;
        while (installer.ClientIsRunning())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(1000, ct);
        }

        return true;
    }

    private static void Record(Version version)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Installer.RecordInstalledVersion(version);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // The uninstall entry is cosmetic; the files are what matter.
            }
        }
    }

    /// <summary>An optional "updateRepository": "owner/name" in client.json points at a fork.</summary>
    public static string? ReadRepository(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("updateRepository", out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } repo
                && RepositoryPattern().IsMatch(repo))
            {
                return repo;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Fall back to the default repository.
        }

        return null;
    }

    private static void WriteStatus(string path, UpdateStatus status)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(status, Json));
        File.Move(temp, path, overwrite: true);
    }

    private static string ThisVersion()
        => VersionText.TryParse(typeof(UpdateRun).Assembly.GetName().Version?.ToString(), out var v) ? VersionText.Short(v) : "0.0.0";

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryPattern();
}

/// <summary>Appends timestamped lines; rolls the file over at one megabyte so it never grows unbounded.</summary>
public sealed class UpdaterLog
{
    private readonly string _path;
    private readonly object _gate = new();

    public UpdaterLog(string path)
    {
        _path = path;
    }

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > 1_000_000)
                {
                    File.Move(_path, _path + ".1", overwrite: true);
                }

                File.AppendAllText(_path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {line}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never stop an update.
            }
        }
    }
}
