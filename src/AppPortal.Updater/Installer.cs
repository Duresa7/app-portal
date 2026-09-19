using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AppPortal.Shared;

namespace AppPortal.Updater;

/// <summary>
/// Downloads, verifies, stages and swaps in a client build. Everything destructive happens in
/// <see cref="Apply"/>, and it is written so a failure half-way puts the old build back.
/// </summary>
public sealed class Installer
{
    private readonly UpdaterPaths _paths;
    private readonly Action<string> _log;
    private readonly Func<string, Version?> _readVersion;

    public Installer(UpdaterPaths paths, Action<string> log, Func<string, Version?>? readVersion = null)
    {
        _paths = paths;
        _log = log;
        _readVersion = readVersion ?? ReadFileVersion;
    }

    public Version? InstalledVersion() => _readVersion(_paths.ClientExe);

    public Version? StagedVersion() => _readVersion(Path.Combine(_paths.StagedDir, "AppPortal.exe"));

    public static Version? ReadFileVersion(string exe)
    {
        if (!File.Exists(exe))
        {
            return null;
        }

        var info = FileVersionInfo.GetVersionInfo(exe);
        return VersionText.TryParse(info.ProductVersion ?? info.FileVersion, out var version) ? version : null;
    }

    /// <summary>
    /// True while a client from this install folder is running. A build somewhere else, such as a
    /// developer's, does not count. When the process cannot be inspected, assume it is ours.
    /// </summary>
    public bool ClientIsRunning()
    {
        foreach (var process in Process.GetProcessesByName("AppPortal"))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || path.StartsWith(_paths.InstallDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Streams the archive to disk while hashing it, then compares the hash with the SHA256SUMS the
    /// release publishes. A release without checksums, or a mismatch, means nothing is kept.
    /// </summary>
    public async Task<string> DownloadAsync(HttpClient http, ReleaseInfo release, CancellationToken ct)
    {
        if (release.ChecksumsUrl is null)
        {
            throw new InvalidDataException($"Release {release.Tag} publishes no {ReleaseFeed.ChecksumsAssetName}; refusing an unverified download.");
        }

        var sums = await http.GetStringAsync(release.ChecksumsUrl, ct);
        var expected = ParseChecksum(sums, ReleaseFeed.ZipAssetName)
                       ?? throw new InvalidDataException($"{ReleaseFeed.ChecksumsAssetName} has no entry for {ReleaseFeed.ZipAssetName}.");

        Directory.CreateDirectory(_paths.WorkDir);
        var zipPath = Path.Combine(_paths.WorkDir, ReleaseFeed.ZipAssetName);
        string actual;
        using (var response = await http.GetAsync(release.ZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(zipPath);
            using var sha = SHA256.Create();
            await using var hashing = new CryptoStream(target, sha, CryptoStreamMode.Write);
            await source.CopyToAsync(hashing, ct);
            await hashing.FlushFinalBlockAsync(ct);
            actual = Convert.ToHexStringLower(sha.Hash!);
        }

        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(zipPath);
            throw new InvalidDataException("The downloaded archive does not match its published SHA-256 and was discarded.");
        }

        _log($"Downloaded {release.Tag}, SHA-256 verified.");
        return zipPath;
    }

    /// <summary>One line of sha256sum output per file: hash, whitespace, name, with an optional binary-mode asterisk.</summary>
    public static string? ParseChecksum(string sumsText, string fileName)
    {
        foreach (var raw in sumsText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length == 64 && string.Equals(parts[1].TrimStart('*'), fileName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0];
            }
        }

        return null;
    }

    /// <summary>Unpacks the archive's client folder into the staged folder, replacing whatever was staged before.</summary>
    public void Stage(string zipPath, Version expected)
    {
        var extract = Path.Combine(_paths.WorkDir, "extract");
        DeleteDirectory(extract);
        ZipFile.ExtractToDirectory(zipPath, extract);
        var client = Path.Combine(extract, "client");
        if (!File.Exists(Path.Combine(client, "AppPortal.exe")))
        {
            DeleteDirectory(extract);
            throw new InvalidDataException("The archive has no client/AppPortal.exe.");
        }

        var found = _readVersion(Path.Combine(client, "AppPortal.exe"));
        if (found is not null && found != expected)
        {
            DeleteDirectory(extract);
            throw new InvalidDataException($"The archive contains {VersionText.Short(found)} but the release says {VersionText.Short(expected)}.");
        }

        DeleteDirectory(_paths.StagedDir);
        Directory.Move(client, _paths.StagedDir);
        DeleteDirectory(extract);
        File.Delete(zipPath);
        _log($"Staged {VersionText.Short(expected)}.");
    }

    /// <summary>
    /// Swaps the staged build in. Windows lets a running executable be renamed but not overwritten,
    /// so the current files move aside into .previous first (this updater's own file included), the
    /// staged files move into place, and .previous goes on the next run once nothing holds it open.
    /// If any move fails, everything that moved aside moves back.
    /// </summary>
    public void Apply()
    {
        if (!Directory.Exists(_paths.StagedDir))
        {
            throw new InvalidOperationException("Nothing is staged.");
        }

        DeleteDirectory(_paths.PreviousDir);
        Directory.CreateDirectory(_paths.PreviousDir);
        var movedAside = new List<(string Parked, string Original)>();
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(_paths.InstallDir))
            {
                var name = Path.GetFileName(entry);
                if (UpdaterPaths.Reserved.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parked = Path.Combine(_paths.PreviousDir, name);
                MoveEntry(entry, parked);
                movedAside.Add((parked, entry));
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(_paths.StagedDir))
            {
                MoveEntry(entry, Path.Combine(_paths.InstallDir, Path.GetFileName(entry)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($"Swap failed, restoring the previous build: {ex.Message}");
            foreach (var (parked, original) in movedAside)
            {
                try
                {
                    DeleteEntry(original);
                    MoveEntry(parked, original);
                }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException)
                {
                    _log($"Could not restore {Path.GetFileName(original)}: {restore.Message}");
                }
            }

            throw;
        }

        DeleteDirectory(_paths.StagedDir);
        CleanPrevious();
    }

    public void DiscardStaged()
    {
        DeleteDirectory(_paths.StagedDir);
        DeleteDirectory(_paths.WorkDir);
    }

    /// <summary>Best effort: the updater that started this run may still be open from inside .previous.</summary>
    public void CleanPrevious()
    {
        try
        {
            DeleteDirectory(_paths.PreviousDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log($".previous stays for now: {ex.Message}");
        }
    }

    [SupportedOSPlatform("windows")]
    public static void RecordInstalledVersion(Version version)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\AppPortalClient", writable: true);
        key?.SetValue("DisplayVersion", VersionText.Short(version));
    }

    private static void MoveEntry(string from, string to)
    {
        if (Directory.Exists(from))
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to, overwrite: true);
        }
    }

    private static void DeleteEntry(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
