using AppPortal.Agent.Executors;
using AppPortal.Shared;

namespace AppPortal.Agent.Tests;

/// <summary>
/// Every manager's command lines, character for character. The table is data, and the only way to
/// know a row is right is to write down what that manager's own documentation says the command is.
/// </summary>
public sealed class PackageManagersTests
{
    private const string Pwsh = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command";

    private const string Ps5Prelude = "[Net.ServicePointManager]::SecurityProtocol = 'Tls12'; "
                                      + "Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force | Out-Null; ";

    [Theory]
    [InlineData("choco", "7zip", null, "machine", "install 7zip -y --no-progress --limit-output")]
    [InlineData("choco", "7zip", "24.9.0", "machine", "install 7zip -y --no-progress --limit-output --version=24.9.0")]
    [InlineData("scoop", "7zip", null, "user", "install 7zip")]
    [InlineData("scoop", "extras/vscode", "1.95.0", "user", "install extras/vscode@1.95.0")]
    [InlineData("scoop", "7zip", null, "machine", "install 7zip --global")]
    [InlineData("npm", "typescript", null, "machine", "install --global typescript")]
    [InlineData("npm", "@angular/cli", "18.2.0", "machine", "install --global @angular/cli@18.2.0")]
    [InlineData("yarn", "typescript", null, "machine", "global add typescript")]
    [InlineData("yarn", "typescript", "5.6.2", "machine", "global add typescript@5.6.2")]
    [InlineData("bun", "typescript", null, "user", "add --global typescript")]
    [InlineData("bun", "typescript", "5.6.2", "user", "add --global typescript@5.6.2")]
    [InlineData("pip", "httpie", null, "machine", "install httpie --disable-pip-version-check --no-input")]
    [InlineData("pip", "httpie", "3.2.4", "user", "install httpie==3.2.4 --user --disable-pip-version-check --no-input")]
    [InlineData("cargo", "ripgrep", null, "user", "install ripgrep")]
    [InlineData("cargo", "ripgrep", "14.1.1", "user", "install ripgrep --version 14.1.1")]
    [InlineData("vcpkg", "zlib", null, "machine", "install zlib")]
    [InlineData("vcpkg", "boost[core,filesystem]", null, "machine", "install boost[core,filesystem]")]
    [InlineData("dotnet-tool", "dotnet-ef", null, "user", "tool install --global dotnet-ef")]
    [InlineData("dotnet-tool", "dotnet-ef", "9.0.0", "user", "tool install --global dotnet-ef --version 9.0.0")]
    [InlineData("powershell-module", "Pester", null, "machine",
        Pwsh + " \"Install-Module -Name Pester -Force -AcceptLicense -Scope AllUsers\"")]
    [InlineData("powershell-module", "Pester", "5.6.1", "user",
        Pwsh + " \"Install-Module -Name Pester -Force -AcceptLicense -Scope CurrentUser -RequiredVersion 5.6.1\"")]
    [InlineData("powershell5-module", "Pester", null, "machine",
        Pwsh + " \"" + Ps5Prelude + "Install-Module -Name Pester -Force -Scope AllUsers\"")]
    public void Each_manager_installs_with_the_command_its_own_documentation_gives(
        string manager, string id, string? version, string scope, string expected)
    {
        Assert.Equal(expected, PackageManagers.Find(manager)!.InstallArguments(id, version, scope, null));
    }

    [Theory]
    [InlineData("choco", "7zip", "machine", "uninstall 7zip -y --limit-output")]
    [InlineData("scoop", "7zip", "user", "uninstall 7zip")]
    [InlineData("scoop", "7zip", "machine", "uninstall 7zip --global")]
    [InlineData("npm", "typescript", "machine", "uninstall --global typescript")]
    [InlineData("yarn", "typescript", "machine", "global remove typescript")]
    [InlineData("bun", "typescript", "user", "remove --global typescript")]
    [InlineData("pip", "httpie", "user", "uninstall httpie --yes --disable-pip-version-check")]
    [InlineData("cargo", "ripgrep", "user", "uninstall ripgrep")]
    [InlineData("vcpkg", "zlib", "machine", "remove zlib")]
    [InlineData("dotnet-tool", "dotnet-ef", "user", "tool uninstall --global dotnet-ef")]
    [InlineData("powershell-module", "Pester", "machine", Pwsh + " \"Uninstall-Module -Name Pester -Force\"")]
    [InlineData("powershell5-module", "Pester", "machine", Pwsh + " \"Uninstall-Module -Name Pester -Force\"")]
    public void Each_manager_removes_with_the_command_its_own_documentation_gives(
        string manager, string id, string scope, string expected)
    {
        Assert.Equal(expected, PackageManagers.Find(manager)!.UninstallArguments(id, scope));
    }

    [Fact]
    public void Every_manager_in_the_table_has_a_command_line_test()
    {
        // A row added to the table without a line above it would be a command line nobody wrote down.
        var tested = new[]
        {
            "choco", "scoop", "npm", "yarn", "bun", "pip", "cargo", "vcpkg", "dotnet-tool",
            "powershell-module", "powershell5-module",
        };
        Assert.Equal(tested.Order(), PackageManagers.All.Select(m => m.Name).Order());
    }

    [Fact]
    public void Extra_arguments_go_inside_a_powershell_command_not_after_it()
    {
        // Appended after the closing quote they would reach powershell.exe itself, which would read
        // -AllowPrerelease as a parameter of its own and refuse to start.
        var arguments = PackageManagers.Find("powershell-module")!.InstallArguments("Pester", null, "machine", "-AllowPrerelease");
        Assert.EndsWith("-Scope AllUsers -AllowPrerelease\"", arguments);
    }

    [Fact]
    public void An_install_that_asks_for_no_version_leaves_no_separator_behind()
    {
        // npm reads "typescript@" as a request for a tag with no name.
        Assert.Equal("install --global typescript", PackageManagers.Find("npm")!.InstallArguments("typescript", "", "machine", null));
        Assert.Equal("install httpie --disable-pip-version-check --no-input", PackageManagers.Find("pip")!.InstallArguments("httpie", null, "machine", null));
    }

    [Theory]
    [InlineData("7zip&calc")]
    [InlineData("7zip|calc")]
    [InlineData("7zip^calc")]
    [InlineData("%PATH%")]
    [InlineData("7zip\"")]
    [InlineData("7zip calc")]
    [InlineData("7zip\ncalc")]
    [InlineData("7zip>out.txt")]
    [InlineData("7zip<in.txt")]
    [InlineData("'7zip'")]
    [InlineData("`calc`")]
    [InlineData("$(calc)")]
    [InlineData(";calc")]
    [InlineData("")]
    public void An_id_a_command_prompt_would_read_as_syntax_is_refused_by_every_manager(string id)
    {
        // npm, Yarn and Scoop are batch files, and Windows runs a batch file through cmd.exe. One
        // character from this list turns a package name into a command running as SYSTEM.
        foreach (var manager in PackageManagers.All)
        {
            var definition = new ManagedPackageDefinition(manager.Name, id, manager.DefaultScope);
            Assert.Throws<InvalidDataException>(definition.Validate);
        }
    }

    [Theory]
    [InlineData("choco", "7zip")]
    [InlineData("choco", "googlechrome")]
    [InlineData("choco", "notepadplusplus.install")]
    [InlineData("scoop", "extras/vscode")]
    [InlineData("npm", "@angular/cli")]
    [InlineData("npm", "typescript")]
    [InlineData("yarn", "@vue/cli")]
    [InlineData("pip", "httpie")]
    [InlineData("pip", "azure-cli")]
    [InlineData("cargo", "cargo-edit")]
    [InlineData("vcpkg", "boost[core,filesystem]")]
    [InlineData("dotnet-tool", "dotnet-ef")]
    [InlineData("powershell-module", "Microsoft.Graph")]
    [InlineData("powershell5-module", "PSWindowsUpdate")]
    public void The_ids_real_catalogues_use_are_accepted(string manager, string id)
    {
        new ManagedPackageDefinition(manager, id, PackageManagers.Find(manager)!.DefaultScope).Validate();
    }

    [Theory]
    [InlineData("1.2.3&calc")]
    [InlineData("1.2 3")]
    [InlineData("latest|calc")]
    public void A_version_is_held_to_the_same_standard_as_the_id(string version)
    {
        Assert.Throws<InvalidDataException>(new ManagedPackageDefinition("npm", "typescript", "machine", version).Validate);
    }

    [Fact]
    public void A_version_for_a_manager_that_cannot_pin_one_is_refused_rather_than_ignored()
    {
        var error = Assert.Throws<InvalidDataException>(new ManagedPackageDefinition("vcpkg", "zlib", "machine", "1.3.1").Validate);
        Assert.Contains("vcpkg", error.Message);
    }

    [Theory]
    [InlineData("cargo", "machine")]
    [InlineData("bun", "machine")]
    [InlineData("dotnet-tool", "machine")]
    [InlineData("choco", "user")]
    [InlineData("vcpkg", "user")]
    public void A_scope_the_manager_cannot_carry_out_is_refused_on_the_page(string manager, string scope)
    {
        Assert.Throws<InvalidDataException>(new ManagedPackageDefinition(manager, "zlib", scope).Validate);
    }

    [Fact]
    public void An_unknown_manager_names_the_ones_that_exist()
    {
        var error = Assert.Throws<InvalidDataException>(new ManagedPackageDefinition("apt", "curl", "machine").Validate);
        Assert.Contains("choco", error.Message);
        Assert.Contains("powershell-module", error.Message);
    }

    [Fact]
    public void The_search_prefers_the_managers_own_folder_and_fills_in_the_profile()
    {
        var environment = new Dictionary<string, string>
        {
            ["USERPROFILE"] = @"C:\Users\ada",
            ["PATH"] = @"C:\old\bin",
        };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Users\ada\scoop\shims\scoop.cmd", @"C:\old\bin\scoop.cmd" };

        Assert.Equal(@"C:\Users\ada\scoop\shims\scoop.cmd",
            PackageManagerSearch.Find(PackageManagers.Find("scoop")!, environment, files.Contains));
    }

    [Fact]
    public void The_search_falls_back_to_path_and_skips_a_probe_whose_variable_has_no_value()
    {
        // %ProgramData% is not in this environment, so that probe cannot be built and must not become
        // a relative path that happens to resolve against the agent's working folder.
        var environment = new Dictionary<string, string> { ["Path"] = @"D:\tools;C:\choco\bin" };
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\choco\bin\choco.exe", @"\chocolatey\bin\choco.exe" };

        Assert.Equal(@"C:\choco\bin\choco.exe", PackageManagerSearch.Find(PackageManagers.Find("choco")!, environment, files.Contains));
    }

    [Fact]
    public void The_search_answers_null_when_the_manager_is_nowhere()
    {
        var environment = new Dictionary<string, string> { ["USERPROFILE"] = @"C:\Users\ada", ["PATH"] = @"C:\Windows" };
        Assert.Null(PackageManagerSearch.Find(PackageManagers.Find("cargo")!, environment, _ => false));
    }
}
