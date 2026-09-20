namespace AppPortal.Agent.Downloads;

/// <summary>
/// Where verified installers are kept between jobs. A game is several gigabytes and a retry, a second
/// person on the same PC, or a fleet built from one image should not fetch it again. The file is named
/// after its own hash, so a cache hit is proof of content rather than a guess from a URL.
/// </summary>
public sealed class InstallerCache(string directory, ILogger logger, long maxBytes = 20L * 1024 * 1024 * 1024, TimeSpan? keepFor = null)
{
    private readonly TimeSpan _keepFor = keepFor ?? TimeSpan.FromDays(7);

    public string Directory => directory;

    public string PathFor(string sha256, string installerType)
        => Path.Combine(directory, $"{sha256.ToLowerInvariant()}.{installerType}");

    /// <summary>
    /// Drops what has gone stale, then the oldest of what is left until the cache is inside its limit.
    /// Called after a download rather than before: evicting first could throw away the very file the
    /// next job is about to ask for.
    /// </summary>
    public void Evict()
    {
        try
        {
            if (!System.IO.Directory.Exists(directory))
            {
                return;
            }

            var files = new System.IO.DirectoryInfo(directory).GetFiles()
                .Where(file => !file.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.LastAccessTimeUtc)
                .ToList();

            var now = DateTime.UtcNow;
            foreach (var file in files.ToList().Where(file => now - file.LastAccessTimeUtc > _keepFor))
            {
                Delete(file);
                files.Remove(file);
            }

            var total = files.Sum(file => file.Length);
            foreach (var file in files.Where(_ => total > maxBytes))
            {
                total -= file.Length;
                Delete(file);
                if (total <= maxBytes)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not tidy the installer cache ({Reason})", ex.GetType().Name);
        }
    }

    /// <summary>
    /// Whether this device has room for the download and what it unpacks into. The check is here
    /// rather than at the end of a two-hour download, where the answer is the same and the wait is not.
    /// </summary>
    public bool HasRoomFor(long sizeBytes, out string reason)
    {
        reason = "";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory)) ?? directory);
            var needed = sizeBytes + Headroom;
            if (drive.AvailableFreeSpace >= needed)
            {
                return true;
            }

            reason = $"This PC needs {Gigabytes(needed)} free to install this and has {Gigabytes(drive.AvailableFreeSpace)}.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An unreadable drive is not a full one. Let the download decide.
            logger.LogWarning("Could not read the free space ({Reason})", ex.GetType().Name);
            return true;
        }
    }

    /// <summary>What an installer wants beyond the download itself, for its own unpacking and logs.</summary>
    private const long Headroom = 1024L * 1024 * 1024;

    private static string Gigabytes(long bytes) => $"{bytes / 1024.0 / 1024 / 1024:0.#} GB";

    private void Delete(System.IO.FileInfo file)
    {
        try
        {
            file.Delete();
            logger.LogInformation("Removed {File} from the installer cache", file.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Something is running it. It will be considered again next time.
        }
    }
}
