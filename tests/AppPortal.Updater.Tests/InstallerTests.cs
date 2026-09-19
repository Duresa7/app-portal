using AppPortal.Shared;
using AppPortal.Updater;

namespace AppPortal.Updater.Tests;

public sealed class InstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-updater-tests", Guid.NewGuid().ToString("N"));
    private readonly UpdaterPaths _paths;
    private readonly List<string> _log = [];

    public InstallerTests()
    {
        _paths = new UpdaterPaths(Path.Combine(_root, "install"), Path.Combine(_root, "state"));
        Directory.CreateDirectory(_paths.InstallDir);
        Directory.CreateDirectory(_paths.StateDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>Version lives in a text file beside the exe, because PE version resources do not read on Linux.</summary>
    private static Version? FakeVersion(string exe)
    {
        var marker = exe + ".version";
        return File.Exists(marker) && VersionText.TryParse(File.ReadAllText(marker), out var v) ? v : null;
    }

    private Installer NewInstaller() => new(_paths, _log.Add, FakeVersion);

    private static void WriteBuild(string dir, string version, params string[] extraFiles)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "AppPortal.exe"), "exe " + version);
        File.WriteAllText(Path.Combine(dir, "AppPortal.exe.version"), version);
        File.WriteAllText(Path.Combine(dir, "AppPortal.Updater.exe"), "updater " + version);
        foreach (var file in extraFiles)
        {
            File.WriteAllText(Path.Combine(dir, file), file + " " + version);
        }
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0.0")]
    [InlineData("0.2.0.0", "0.2.0.0")]
    [InlineData("0.2.0+abc123", "0.2.0.0")]
    [InlineData("1.4", "1.4.0.0")]
    [InlineData("v2.0.1-rc1", "2.0.1.0")]
    public void Version_text_normalises_to_four_parts(string text, string expected)
    {
        Assert.True(VersionText.TryParse(text, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Fact]
    public void Version_text_rejects_garbage_and_compares_correctly()
    {
        Assert.False(VersionText.TryParse("latest", out _));
        Assert.False(VersionText.TryParse("", out _));
        Assert.True(VersionText.IsNewer("v0.2.0", "0.1.1.0"));
        Assert.False(VersionText.IsNewer("0.2.0", "0.2.0.0"));
        Assert.False(VersionText.IsNewer(null, "0.2.0"));
    }

    [Fact]
    public void Checksum_file_yields_the_archive_hash_in_either_mode()
    {
        var hash = new string('a', 64);
        var sums = $"# comment\n{hash}  {ReleaseFeed.ZipAssetName}\n{new string('b', 64)} *other.zip\n";
        Assert.Equal(hash, Installer.ParseChecksum(sums, ReleaseFeed.ZipAssetName));
        Assert.Equal(new string('b', 64), Installer.ParseChecksum(sums, "other.zip"));
        Assert.Null(Installer.ParseChecksum(sums, "missing.zip"));
        Assert.Null(Installer.ParseChecksum("short  " + ReleaseFeed.ZipAssetName, ReleaseFeed.ZipAssetName));
    }

    [Fact]
    public void Apply_swaps_the_staged_build_in_and_parks_the_old_one()
    {
        WriteBuild(_paths.InstallDir, "0.1.1", "old-only.dll", "Uninstall-AppPortalClient.ps1");
        WriteBuild(_paths.StagedDir, "0.2.0", "new-only.dll", "Uninstall-AppPortalClient.ps1");
        Directory.CreateDirectory(_paths.WorkDir);
        File.WriteAllText(Path.Combine(_paths.WorkDir, "leftover.zip"), "x");

        var installer = NewInstaller();
        Assert.Equal(Version.Parse("0.1.1.0"), installer.InstalledVersion());
        Assert.Equal(Version.Parse("0.2.0.0"), installer.StagedVersion());

        installer.Apply();

        Assert.Equal(Version.Parse("0.2.0.0"), installer.InstalledVersion());
        Assert.Equal("exe 0.2.0", File.ReadAllText(_paths.ClientExe));
        Assert.True(File.Exists(Path.Combine(_paths.InstallDir, "new-only.dll")));
        Assert.False(File.Exists(Path.Combine(_paths.InstallDir, "old-only.dll")), "a file from the old build must not survive the swap");
        Assert.False(Directory.Exists(_paths.StagedDir));
        Assert.False(Directory.Exists(_paths.PreviousDir), "nothing held the old files open, so .previous is gone");
        Assert.True(Directory.Exists(_paths.WorkDir), "reserved folders are never moved");
    }

    [Fact]
    public void Apply_restores_the_old_build_when_a_move_fails()
    {
        WriteBuild(_paths.InstallDir, "0.1.1", "keep.dll");
        WriteBuild(_paths.StagedDir, "0.2.0");
        // A staged entry that collides with a reserved folder cannot be moved into place; the swap must undo itself.
        Directory.CreateDirectory(_paths.WorkDir);
        File.WriteAllText(Path.Combine(_paths.WorkDir, "busy.txt"), "x");
        Directory.CreateDirectory(Path.Combine(_paths.StagedDir, ".update"));
        File.WriteAllText(Path.Combine(_paths.StagedDir, ".update", "trap.txt"), "x");

        var installer = NewInstaller();
        Assert.ThrowsAny<IOException>(() => installer.Apply());

        Assert.Equal(Version.Parse("0.1.1.0"), installer.InstalledVersion());
        Assert.Equal("exe 0.1.1", File.ReadAllText(_paths.ClientExe));
        Assert.True(File.Exists(Path.Combine(_paths.InstallDir, "keep.dll")));
        Assert.Contains(_log, line => line.Contains("restoring the previous build"));
    }

    [Fact]
    public void Stage_rejects_an_archive_without_the_client_and_a_version_that_does_not_match()
    {
        Directory.CreateDirectory(_paths.WorkDir);
        var bad = Path.Combine(_paths.WorkDir, "bad.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(bad, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("client/readme.txt");
        }

        var installer = NewInstaller();
        Assert.Throws<InvalidDataException>(() => installer.Stage(bad, Version.Parse("0.2.0.0")));

        var mismatched = Path.Combine(_paths.WorkDir, "mismatch.zip");
        var source = Path.Combine(_root, "source");
        WriteBuild(Path.Combine(source, "client"), "0.3.0");
        System.IO.Compression.ZipFile.CreateFromDirectory(source, mismatched);
        Assert.Throws<InvalidDataException>(() => installer.Stage(mismatched, Version.Parse("0.2.0.0")));
        Assert.False(Directory.Exists(_paths.StagedDir));

        installer.Stage(mismatched, Version.Parse("0.3.0.0"));
        Assert.Equal(Version.Parse("0.3.0.0"), installer.StagedVersion());
        Assert.False(File.Exists(mismatched), "the archive is deleted once staged");
    }

    [Fact]
    public void Repository_override_is_read_only_when_well_formed()
    {
        var settings = _paths.SettingsPath;
        File.WriteAllText(settings, """{ "serverUrl": "http://x", "updateRepository": "someone/fork" }""");
        Assert.Equal("someone/fork", UpdateRun.ReadRepository(settings));
        File.WriteAllText(settings, """{ "updateRepository": "https://evil.example/x" }""");
        Assert.Null(UpdateRun.ReadRepository(settings));
        File.WriteAllText(settings, "not json");
        Assert.Null(UpdateRun.ReadRepository(settings));
        Assert.Null(UpdateRun.ReadRepository(Path.Combine(_root, "missing.json")));
    }
}
