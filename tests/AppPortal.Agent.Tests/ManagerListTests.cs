using AppPortal.Agent.Executors;
using AppPortal.Shared;

namespace AppPortal.Agent.Tests;

/// <summary>
/// Each manager's list command, as its output looks on a real PC. The transcripts carry the noise a
/// real run carries, because the error stream is read together with the output.
/// </summary>
public sealed class ManagerListTests
{
    [Fact]
    public void Chocolatey_prints_one_pipe_separated_row_per_package()
    {
        const string output = """
            chocolatey|2.3.0
            git|2.46.0
            git.install|2.46.0
            """;

        Assert.Equal(
            Rows(("chocolatey", "2.3.0"), ("git", "2.46.0"), ("git.install", "2.46.0")),
            ManagerList.Parse("choco", output));
    }

    [Fact]
    public void Scoop_prints_a_table_and_the_date_in_it_does_not_become_the_version()
    {
        const string output = """
            Installed apps:

            Name  Version Source Updated             Info
            ----  ------- ------ -------             ----
            7zip  24.08   main   2024-08-12 10:11:12
            git   2.46.0  main   2024-08-12 10:12:13 Global install
            """;

        Assert.Equal(Rows(("7zip", "24.08"), ("git", "2.46.0")), ManagerList.Parse("scoop", output));
    }

    [Fact]
    public void Scoop_with_nothing_installed_is_an_empty_list_rather_than_an_unreadable_one()
    {
        Assert.Equal(Rows(), ManagerList.Parse("scoop", "WARN  There aren't any apps installed.\r\n"));
    }

    [Fact]
    public void Npm_is_read_from_its_json_even_with_a_warning_in_front_of_it()
    {
        const string output = """
            npm warn config global `--global`, `--local` are deprecated. Use `--location=global` instead.
            {
              "name": "npm",
              "dependencies": {
                "@angular/cli": { "version": "18.2.1", "overridden": false },
                "typescript": { "version": "5.5.4", "overridden": false }
              }
            }
            """;

        Assert.Equal(Rows(("@angular/cli", "18.2.1"), ("typescript", "5.5.4")), ManagerList.Parse("npm", output));
    }

    [Fact]
    public void Npm_with_no_global_packages_is_empty_and_npm_that_printed_no_json_is_unreadable()
    {
        Assert.Equal(Rows(), ManagerList.Parse("npm", """{ "name": "npm" }"""));
        Assert.Null(ManagerList.Parse("npm", "npm error code ENOENT"));
    }

    [Fact]
    public void Yarn_names_each_package_on_its_binaries_line()
    {
        const string output = """
            yarn global v1.22.22
            info "@vue/cli@5.0.8" has binaries:
               - vue
            info "typescript@5.5.4" has binaries:
               - tsc
               - tsserver
            Done in 0.12s.
            """;

        Assert.Equal(Rows(("@vue/cli", "5.0.8"), ("typescript", "5.5.4")), ManagerList.Parse("yarn", output));
    }

    [Fact]
    public void Bun_draws_a_tree_and_a_scoped_package_keeps_its_own_at_sign()
    {
        const string output = """
            C:\Users\ada\.bun\install\global node_modules (3)
            ├── @biomejs/biome@1.8.3
            ├── cowsay@1.6.0
            └── typescript@5.5.4
            """;

        Assert.Equal(
            Rows(("@biomejs/biome", "1.8.3"), ("cowsay", "1.6.0"), ("typescript", "5.5.4")),
            ManagerList.Parse("bun", output));
    }

    [Fact]
    public void Pip_is_read_from_its_json()
    {
        const string output = """
            [{"name": "pip", "version": "24.2"}, {"name": "requests", "version": "2.32.3"}]
            """;

        Assert.Equal(Rows(("pip", "24.2"), ("requests", "2.32.3")), ManagerList.Parse("pip", output));
        Assert.Null(ManagerList.Parse("pip", "ERROR: unknown option --format"));
    }

    [Fact]
    public void Cargo_lists_each_crate_above_the_binaries_it_installed()
    {
        const string output = """
            cargo-edit v0.12.3:
                cargo-add.exe
                cargo-rm.exe
            ripgrep v14.1.0 (C:\src\ripgrep):
                rg.exe
            """;

        Assert.Equal(Rows(("cargo-edit", "0.12.3"), ("ripgrep", "14.1.0")), ManagerList.Parse("cargo", output));
    }

    [Fact]
    public void Vcpkg_names_the_port_without_its_triplet_or_features()
    {
        const string output = """
            curl:x64-windows                                  8.9.1#1          A library for transferring data with URLs
            curl[ssl]:x64-windows                                              Default SSL backend
            zlib:x64-windows                                  1.3.1            A compression library
            """;

        Assert.Equal(Rows(("curl", "8.9.1#1"), ("zlib", "1.3.1")), ManagerList.Parse("vcpkg", output));
    }

    [Fact]
    public void A_dotnet_tool_table_is_read_past_its_rule()
    {
        const string output = """
            Package Id      Version      Commands
            -------------------------------------------
            dotnetsay       2.1.7        dotnetsay
            dotnet-ef       8.0.8        dotnet-ef
            """;

        Assert.Equal(Rows(("dotnetsay", "2.1.7"), ("dotnet-ef", "8.0.8")), ManagerList.Parse("dotnet-tool", output));
    }

    [Theory]
    [InlineData("powershell-module")]
    [InlineData("powershell5-module")]
    public void A_powershell_module_list_is_read_from_csv_without_its_header(string manager)
    {
        const string output = """
            "Name","Version"
            "PSReadLine","2.3.5"
            "Pester","5.6.1"
            """;

        Assert.Equal(Rows(("PSReadLine", "2.3.5"), ("Pester", "5.6.1")), ManagerList.Parse(manager, output));
    }

    [Fact]
    public void Every_manager_this_build_knows_has_a_reader()
    {
        // A manager added to the table without one would install, and then never show as installed.
        // The JSON readers need a document to call a list empty; the rest need nothing at all.
        Assert.All(PackageManagers.All, manager => Assert.NotNull(ManagerList.Parse(manager.Name,
            manager.Name switch { "npm" => "{}", "pip" => "[]", _ => "" })));
    }

    [Fact]
    public void A_manager_this_build_has_never_heard_of_is_not_read()
    {
        Assert.Null(ManagerList.Parse("apt", "vim/stable 9.0 amd64"));
    }

    private static InstalledSoftware[] Rows(params (string Name, string Version)[] rows)
        => rows.Select(row => new InstalledSoftware(row.Name, row.Version)).ToArray();
}
