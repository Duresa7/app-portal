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

    [Fact]
    public void The_dependencies_are_the_newest_folder_of_each_package_the_manifest_names()
    {
        // The App Installer 1.29 manifest from a fresh Windows 11 25H2 machine, cut to the parts read.
        var winget = Package("Microsoft.DesktopAppInstaller_1.29.379.0_x64__8wekyb3d8bbwe");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(winget)!, "AppxManifest.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Microsoft.DesktopAppInstaller" Publisher="CN=Microsoft Corporation" Version="1.29.379.0" ProcessorArchitecture="x64" />
              <Dependencies>
                <PackageDependency Name="Microsoft.WindowsAppRuntime.1.8" MinVersion="8000.616.304.0" Publisher="CN=Microsoft Corporation" />
                <PackageDependency Name="Microsoft.VCLibs.140.00" MinVersion="14.0.33519.0" Publisher="CN=Microsoft Corporation" />
                <PackageDependency Name="Microsoft.VCLibs.140.00.UWPDesktop" MinVersion="14.0.33728.0" Publisher="CN=Microsoft Corporation" />
              </Dependencies>
            </Package>
            """);
        Folder("Microsoft.WindowsAppRuntime.1.8_8000.616.304.0_x64__8wekyb3d8bbwe");
        var runtime = Folder("Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_x64__8wekyb3d8bbwe");
        Folder("Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_x86__8wekyb3d8bbwe");
        Folder("Microsoft.WindowsAppRuntime.1.7_7000.785.2325.0_x64__8wekyb3d8bbwe");
        var vclibs = Folder("Microsoft.VCLibs.140.00_14.0.33519.0_x64__8wekyb3d8bbwe");
        Folder("Microsoft.VCLibs.140.00_14.0.33519.0_x86__8wekyb3d8bbwe");
        var desktop = Folder("Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64__8wekyb3d8bbwe");

        var dependencies = new WingetLocator(_root).Dependencies(winget);

        Assert.Equal<string>([runtime, vclibs, desktop], dependencies);
    }

    [Fact]
    public void A_dependency_that_is_not_installed_leaves_the_others()
    {
        var winget = Package("Microsoft.DesktopAppInstaller_1.29.379.0_x64__8wekyb3d8bbwe");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(winget)!, "AppxManifest.xml"), """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Microsoft.DesktopAppInstaller" ProcessorArchitecture="x64" />
              <Dependencies>
                <PackageDependency Name="Microsoft.UI.Xaml.2.8" />
                <PackageDependency Name="Microsoft.VCLibs.140.00.UWPDesktop" />
              </Dependencies>
            </Package>
            """);
        var desktop = Folder("Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64__8wekyb3d8bbwe");

        Assert.Equal<string>([desktop], new WingetLocator(_root).Dependencies(winget));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not xml")]
    public void Without_a_readable_manifest_there_is_nothing_to_add(string? manifest)
    {
        var winget = Package("Microsoft.DesktopAppInstaller_1.29.379.0_x64__8wekyb3d8bbwe");
        if (manifest is not null)
        {
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(winget)!, "AppxManifest.xml"), manifest);
        }

        Folder("Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64__8wekyb3d8bbwe");

        Assert.Empty(new WingetLocator(_root).Dependencies(winget));
    }

    [Fact]
    public void An_account_with_an_alias_gets_it()
    {
        var profile = Path.Combine(_root, "Users", "apptester");
        var alias = Path.Combine(profile, "AppData", "Local", "Microsoft", "WindowsApps", "winget.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
        File.WriteAllText(alias, "");

        Assert.Equal(alias, new WingetLocator(_root, account => account == @"PCpptester" ? profile : null).ForAccount(@"PCpptester"));
    }

    [Fact]
    public void An_account_without_an_alias_or_a_profile_gets_nothing()
    {
        // Not registered yet, which is how a new account starts, or no profile on this PC at all.
        var profile = Path.Combine(_root, "Users", "newcomer");
        Directory.CreateDirectory(profile);
        var locator = new WingetLocator(_root, account => account == @"PC
ewcomer" ? profile : null);

        Assert.Null(locator.ForAccount(@"PC
ewcomer"));
        Assert.Null(locator.ForAccount(@"PC
obody"));
    }

    private string Folder(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        return directory;
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
