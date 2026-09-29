using System.Text.Json;

using AppPortal.Server.Catalog;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

/// <summary>
/// A zip the agent unpacks. Each text field ends up inside a PowerShell literal, so what the rules let
/// through matters as much as what they stop.
/// </summary>
public sealed class PortablePackageDefinitionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static PortablePackageDefinition Portable => new("https://vendor.example/tool-2.1.zip", new string('b', 64),
        12_000_000, "Vendor Tool", @"Vendor Tool 2.1\bin\tool.exe", ShortcutName: "Vendor Tool", Version: "2.1.0");

    [Fact]
    public void Portable_json_keeps_its_shape_and_its_size()
    {
        PackageDefinition definition = Portable;
        definition.Validate();
        var json = JsonSerializer.Serialize(definition, Json);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(["kind", "url", "sha256", "sizeBytes", "folder", "executable", "scope", "shortcutName", "version", "requiresReboot"],
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("portable", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal("portable", definition.Kind);
        Assert.Equal(12_000_000, definition.DownloadSizeBytes);
        Assert.Equal(definition, JsonSerializer.Deserialize<PackageDefinition>(json, Json));
    }

    [Fact]
    public void Leaving_out_the_shortcut_and_version_writes_them_as_null()
    {
        var bare = new PortablePackageDefinition("https://vendor.example/tool.zip", new string('c', 64), 1, "Tool", "tool.exe");
        bare.Validate();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize<PackageDefinition>(bare, Json));

        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("shortcutName").ValueKind);
        Assert.Equal("Tool", bare.DisplayName);
        Assert.Equal("Vendor Tool", Portable.DisplayName);
    }

    [Theory]
    [InlineData("tool.exe")]
    [InlineData(@"bin\tool.exe")]
    [InlineData("bin/tool.exe")]
    [InlineData(@"Tool 2.1\x64\tool-cli_v2.EXE")]
    public void Paths_inside_the_archive_are_accepted(string executable)
    {
        (Portable with { Executable = executable }).Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"..\tool.exe")]
    [InlineData(@"bin\..\..\tool.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"\tool.exe")]
    [InlineData("tool.bat")]
    [InlineData("tool.exe'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("to\"ol.exe")]
    [InlineData(@"bin.\tool.exe")]
    [InlineData(@"CON\tool.exe")]
    [InlineData("$(evil).exe")]
    public void Anything_that_is_not_a_plain_path_to_a_program_is_refused(string executable)
    {
        Assert.Throws<InvalidDataException>(() => (Portable with { Executable = executable }).Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" Tool")]
    [InlineData("Tool.")]
    [InlineData("Tool ")]
    [InlineData(@"Tools\Nested")]
    [InlineData("It's")]
    [InlineData("NUL")]
    [InlineData("com1.txt")]
    public void A_folder_must_be_one_plain_name(string folder)
    {
        Assert.Throws<InvalidDataException>(() => (Portable with { Folder = folder }).Validate());
    }

    [Theory]
    [InlineData("It's mine")]
    [InlineData("Tool.")]
    [InlineData("A/B")]
    public void A_shortcut_name_follows_the_same_rule(string shortcut)
    {
        Assert.Throws<InvalidDataException>(() => (Portable with { ShortcutName = shortcut }).Validate());
    }

    [Theory]
    [InlineData("1.0'")]
    [InlineData("v 2")]
    [InlineData(".1")]
    public void A_version_is_letters_digits_and_dots(string version)
    {
        Assert.Throws<InvalidDataException>(() => (Portable with { Version = version }).Validate());
    }

    [Fact]
    public void The_download_is_held_to_the_same_rules_as_a_direct_installer()
    {
        Assert.Throws<InvalidDataException>(() => (Portable with { Url = "ftp://vendor.example/tool.zip" }).Validate());
        Assert.Throws<InvalidDataException>(() => (Portable with { Url = "https://user:secret@vendor.example/tool.zip" }).Validate());
        Assert.Throws<InvalidDataException>(() => (Portable with { Sha256 = "abc" }).Validate());
        Assert.Throws<InvalidDataException>(() => (Portable with { SizeBytes = 0 }).Validate());
        Assert.Throws<InvalidDataException>(() => (Portable with { Scope = "everyone" }).Validate());
    }

    [Fact]
    public void A_portable_app_survives_export_and_import_unchanged()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        var portable = Portable with { Scope = "user", RequiresReboot = true };
        store.Upsert(new CatalogEntry { Id = "vendor-tool", Name = "Vendor Tool", Agent = portable });

        var reimported = CatalogStore.Parse(store.ExportJson());

        Assert.Equal(portable, Assert.Single(reimported).Agent);
        Assert.Equal(12_000_000, Assert.Single(reimported).ToPublic().DownloadSizeBytes);
    }

    [Fact]
    public void The_catalog_says_where_a_portable_app_comes_from()
    {
        var entry = new CatalogEntry { Id = "vendor-tool", Name = "Vendor Tool", Agent = Portable };

        Assert.Equal("Portable app (zip)", CatalogEntry.SourceName(Portable));
        Assert.Equal("Installs Vendor Tool for everyone on the PC, by unpacking it from vendor.example.", entry.Describe());
    }
}
