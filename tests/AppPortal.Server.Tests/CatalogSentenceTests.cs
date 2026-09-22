using AppPortal.Server.Catalog;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

/// <summary>
/// The sentence under the catalog form. It names the app, who it installs for and where it comes
/// from, for every source, so an administrator reads one line instead of six fields.
/// </summary>
public sealed class CatalogSentenceTests
{
    private static CatalogEntry App(PackageDefinition? agent = null, string action1 = "") => new()
    {
        Id = "app",
        Name = "Google Chrome",
        Agent = agent,
        Action1 = new Action1PackageRef { PackageId = action1 },
    };

    [Fact]
    public void Action1()
    {
        Assert.Equal("Installs Google Chrome for everyone on the PC, through Action1.",
            App(action1: "Google_Chrome_builtin").Describe());
    }

    [Fact]
    public void Winget()
    {
        Assert.Equal("Installs Google Chrome for everyone on the PC, through winget.",
            App(new WingetPackageDefinition("Google.Chrome", "machine")).Describe());
    }

    [Fact]
    public void The_microsoft_store()
    {
        Assert.Equal("Installs Google Chrome for the person who asks for it, from the Microsoft Store.",
            App(new WingetPackageDefinition("9WZDNCRFJ3TJ", "user", Source: WingetSources.Store)).Describe());
    }

    [Fact]
    public void A_package_manager_by_its_own_name()
    {
        Assert.Equal("Installs Google Chrome for everyone on the PC, through Chocolatey.",
            App(new ManagedPackageDefinition("choco", "googlechrome", "machine")).Describe());
        Assert.Equal("Installs Google Chrome for the person who asks for it, through Scoop.",
            App(new ManagedPackageDefinition("scoop", "extras/googlechrome", "user")).Describe());
    }

    [Fact]
    public void A_direct_download_names_where_it_is_downloaded_from()
    {
        Assert.Equal("Installs Google Chrome for everyone on the PC, with its own installer from vendor.example.",
            App(PackageDefinitionTests.Direct).Describe());
    }

    [Fact]
    public void Both_packages_say_which_device_gets_which_and_what_the_override_decides()
    {
        var both = App(new WingetPackageDefinition("Google.Chrome", "user"), "Google_Chrome_builtin");
        Assert.Equal("Installs Google Chrome for everyone on the PC through Action1, or for the person who asks for it, "
                     + "through winget on a device with only the agent.", both.Describe());

        both.EngineOverride = "agent";
        Assert.EndsWith("A device that could use either uses the agent.", both.Describe());
    }

    [Fact]
    public void An_app_with_no_source_says_nothing_can_install_it()
    {
        Assert.Equal("Google Chrome has no source yet, so no device can install it.", App().Describe());
        Assert.StartsWith("this app has no source", new CatalogEntry().Describe());
    }
}
