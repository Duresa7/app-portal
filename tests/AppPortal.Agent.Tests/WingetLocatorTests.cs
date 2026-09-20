using AppPortal.Agent.Executors;

namespace AppPortal.Agent.Tests;

public sealed class WingetLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));

    public WingetLocatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void The_newest_installed_package_wins()
    {
        // Windows keeps every version it has staged, so more than one is the normal case rather than
        // a broken machine, and picking by string order would rank 1.9 above 1.22.
        Package("Microsoft.DesktopAppInstaller_1.9.25200.0_x64__8wekyb3d8bbwe");
        var newest = Package("Microsoft.DesktopAppInstaller_1.22.11141.0_x64__8wekyb3d8bbwe");
        Package("Microsoft.DesktopAppInstaller_1.20.10101.0_x64__8wekyb3d8bbwe");

        Assert.Equal(newest, new WingetLocator(_root).Find());
    }

    [Fact]
    public void A_directory_without_the_executable_is_not_offered()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Microsoft.DesktopAppInstaller_2.0.0.0_x64__8wekyb3d8bbwe"));
        var real = Package("Microsoft.DesktopAppInstaller_1.22.11141.0_x64__8wekyb3d8bbwe");

        Assert.Equal(real, new WingetLocator(_root).Find());
    }

    [Fact]
    public void Other_packages_in_the_same_folder_are_ignored()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Microsoft.WindowsTerminal_1.0.0.0_x64__8wekyb3d8bbwe"));
        File.WriteAllText(Path.Combine(_root, "Microsoft.WindowsTerminal_1.0.0.0_x64__8wekyb3d8bbwe", "winget.exe"), "");

        Assert.Null(new WingetLocator(_root).Find());
    }

    [Fact]
    public void A_missing_folder_is_not_an_error()
    {
        // WindowsApps denies everything but SYSTEM, so a developer run legitimately sees nothing here.
        Assert.Null(new WingetLocator(Path.Combine(_root, "absent")).Find());
    }

    [Theory]
    [InlineData("Microsoft.DesktopAppInstaller_1.22.11141.0_x64__8wekyb3d8bbwe", "1.22.11141.0")]
    [InlineData("Microsoft.DesktopAppInstaller__x64__8wekyb3d8bbwe", "0.0")]
    [InlineData("nonsense", "0.0")]
    public void An_unparsable_name_sorts_last_rather_than_throwing(string name, string expected)
    {
        Assert.Equal(Version.Parse(expected), WingetLocator.VersionOf(Path.Combine(_root, name)));
    }

    private string Package(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        var executable = Path.Combine(directory, "winget.exe");
        File.WriteAllText(executable, "");
        return executable;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
