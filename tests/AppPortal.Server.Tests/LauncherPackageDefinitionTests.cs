using System.Text.Json;

using AppPortal.Server.Catalog;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

/// <summary>A game handed to its launcher. The id reaches a command line, so its rule is the part that matters.</summary>
public sealed class LauncherPackageDefinitionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Launcher_json_keeps_its_shape()
    {
        PackageDefinition definition = new LauncherPackageDefinition("steam", "730");
        definition.Validate();
        var json = JsonSerializer.Serialize(definition, Json);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(["kind", "launcher", "gameId", "scope", "requiresReboot"],
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("launcher", definition.Kind);
        Assert.Equal("user", definition.Scope);
        Assert.Null(definition.DownloadSizeBytes);
        Assert.Equal(definition, JsonSerializer.Deserialize<PackageDefinition>(json, Json));
    }

    [Theory]
    [InlineData("steam", "730")]
    [InlineData("steam", "1172470")]
    [InlineData("epic", "Fortnite")]
    [InlineData("epic", "fn:4fe75bbc5a674f4f9b356b5c90567da5:Fortnite")]
    [InlineData("gog", "1207658924")]
    [InlineData("ubisoft", "5271")]
    [InlineData("STEAM", "730")]
    public void Real_game_ids_are_accepted(string launcher, string id)
    {
        new LauncherPackageDefinition(launcher, id).Validate();
    }

    [Theory]
    [InlineData("steam", "")]
    [InlineData("steam", "730 && calc")]
    [InlineData("steam", "cs2")]
    [InlineData("steam", "730/../../x")]
    [InlineData("epic", "Fortnite?action=uninstall")]
    [InlineData("epic", "Fort\"nite")]
    [InlineData("epic", "-Fortnite")]
    [InlineData("gog", "12 34")]
    [InlineData("ubisoft", "5271/1")]
    public void Anything_but_a_plain_id_is_refused(string launcher, string id)
    {
        Assert.Throws<InvalidDataException>(() => new LauncherPackageDefinition(launcher, id).Validate());
    }

    [Fact]
    public void An_unknown_launcher_names_the_ones_there_are()
    {
        var error = Assert.Throws<InvalidDataException>(() => new LauncherPackageDefinition("battlenet", "WoW").Validate());

        Assert.Contains("steam, epic, gog, ubisoft", error.Message);
    }

    [Fact]
    public void A_game_is_always_for_one_person()
    {
        var error = Assert.Throws<InvalidDataException>(() => new LauncherPackageDefinition("steam", "730", "machine").Validate());

        Assert.Contains("one person's session", error.Message);
    }

    [Fact]
    public void Each_launcher_builds_its_documented_links()
    {
        var steam = GameLaunchers.Find("steam")!;

        Assert.Equal("steam://install/730", steam.Uri(steam.InstallUri, "730"));
        Assert.Equal("steam://uninstall/730", steam.Uri(steam.UninstallUri!, "730"));
        Assert.Equal("steam://validate/730", steam.Uri(steam.RepairUri!, "730"));
        Assert.All(GameLaunchers.All, launcher => Assert.Contains("{id}", launcher.InstallUri));
        Assert.All(GameLaunchers.All, launcher => Assert.StartsWith(launcher.Scheme + "://", launcher.InstallUri));
    }

    [Fact]
    public void The_catalog_says_the_game_is_opened_in_its_launcher_and_not_installed()
    {
        var entry = new CatalogEntry { Id = "cs2", Name = "Counter-Strike 2", Agent = new LauncherPackageDefinition("steam", "730") };

        Assert.Equal("Steam", CatalogEntry.SourceName(entry.Agent));
        Assert.Equal("Opens Counter-Strike 2 in Steam for the person who asks for it. They finish in Steam with their own account.",
            entry.Describe());
        Assert.Equal("Steam", entry.ToPublic().HandoffTo);
        Assert.Equal("user", entry.ToPublic().InstallScope);
    }

    [Fact]
    public void An_app_that_is_not_a_game_has_no_launcher_to_hand_to()
    {
        var entry = new CatalogEntry { Id = "steam", Name = "Steam", Agent = new WingetPackageDefinition("Valve.Steam", "machine") };

        Assert.Null(entry.ToPublic().HandoffTo);
    }

    [Fact]
    public void A_game_survives_export_and_import_unchanged()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        var game = new LauncherPackageDefinition("epic", "Fortnite", RequiresReboot: true);
        store.Upsert(new CatalogEntry { Id = "fortnite", Name = "Fortnite", Agent = game });

        var reimported = CatalogStore.Parse(store.ExportJson());

        Assert.Equal(game, Assert.Single(reimported).Agent);
    }
}
