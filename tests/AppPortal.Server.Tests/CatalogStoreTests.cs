using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Catalog;
using AppPortal.Server.Data;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

public sealed class CatalogStoreTests
{
    [Fact]
    public void Parse_rejects_duplicates_and_missing_package_ids()
    {
        Assert.Throws<InvalidDataException>(() => CatalogStore.Parse("""
            { "apps": [ { "id": "a", "name": "A", "action1": { "packageId": "x" } }, { "id": "A", "name": "A2", "action1": { "packageId": "y" } } ] }
            """));
        Assert.Throws<InvalidDataException>(() => CatalogStore.Parse("""
            { "apps": [ { "id": "a", "name": "A" } ] }
            """));
    }

    [Fact]
    public void An_agent_definition_may_name_its_kind_anywhere()
    {
        // A PowerShell hashtable, or a person editing by hand, does not keep "kind" first.
        var app = Assert.Single(CatalogStore.Parse("""
            { "apps": [ { "id": "yaml", "name": "YAML",
              "agent": { "manager": "powershell5-module", "id": "powershell-yaml", "scope": "machine", "kind": "managed" } } ] }
            """));

        Assert.Equal("powershell-yaml", Assert.IsType<ManagedPackageDefinition>(app.Agent).Id);
    }

    [Fact]
    public void Match_rules_fall_back_to_the_app_name()
    {
        var entries = CatalogStore.Parse("""
            { "apps": [
                { "id": "chrome", "name": "Google Chrome", "action1": { "packageId": "x" } },
                { "id": "code", "name": "Visual Studio Code", "action1": { "packageId": "y" }, "match": { "nameEquals": "Microsoft Visual Studio Code (User)" } }
            ] }
            """);
        Assert.True(entries[0].MatchesInstalled("Google Chrome 128"));
        Assert.False(entries[0].MatchesInstalled("Chromium"));
        Assert.True(entries[1].MatchesInstalled("microsoft visual studio code (user)"));
        Assert.False(entries[1].MatchesInstalled("Visual Studio Code"));
    }

    [Fact]
    public void An_export_re_imports_to_the_same_catalog()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, Path.Combine(test.Root, "catalog.json"));
        var source = File.ReadAllText(CheckedInCatalog);
        store.Import(CatalogStore.Parse(source));

        var exported = store.ExportJson();
        var fresh = new Database(Path.Combine(test.Root, "second.db"));
        fresh.Migrate();
        var second = new CatalogStore(fresh, "");
        second.Import(CatalogStore.Parse(exported));

        Assert.Equal(store.ExportJson(), second.ExportJson());
        Assert.Equal(CatalogStore.Parse(source).Count, second.Count());
    }

    [Fact]
    public void Importing_again_updates_an_app_in_place_and_leaves_the_others()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        store.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "a", "name": "A", "action1": { "packageId": "x" } },
                        { "id": "b", "name": "B", "action1": { "packageId": "y" } } ] }
            """));

        store.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "a", "name": "A renamed", "category": "Tools", "action1": { "packageId": "x2", "version": "1.0" } } ] }
            """));

        Assert.Equal(2, store.Count());
        var a = store.Find("A")!;
        Assert.Equal("A renamed", a.Name);
        Assert.Equal("Tools", a.Category);
        Assert.Equal("x2", a.Action1.PackageId);
        Assert.Equal("1.0", a.Action1.Version);
        Assert.Equal("B", store.Find("b")!.Name);
    }

    [Fact]
    public void Upsert_writes_one_app_and_reads_it_back_whole()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");

        store.Upsert(new CatalogEntry
        {
            Id = "slack",
            Name = "Slack",
            Publisher = "Slack Technologies",
            Description = "Chat.",
            Category = "Communication",
            IconUrl = "https://example.invalid/slack.png",
            Featured = true,
            Match = new MatchRule { NameContains = "Slack" },
            Action1 = new Action1PackageRef { PackageId = "Slack_123", Version = "2.1" },
        });

        var stored = store.Find("slack")!;
        Assert.Equal("Slack", stored.Name);
        Assert.Equal("Slack Technologies", stored.Publisher);
        Assert.Equal("Communication", stored.Category);
        Assert.Equal("https://example.invalid/slack.png", stored.IconUrl);
        Assert.True(stored.Featured);
        Assert.False(stored.Hidden);
        Assert.Equal("Slack", stored.Match!.NameContains);
        Assert.Equal("Slack_123", stored.Action1.PackageId);
        Assert.Equal("2.1", stored.Action1.Version);
    }

    [Fact]
    public void Upsert_refuses_an_app_with_no_id_name_or_package()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        var valid = new CatalogEntry { Id = "a", Name = "A", Action1 = new Action1PackageRef { PackageId = "x" } };

        Assert.Throws<InvalidDataException>(() => store.Upsert(new CatalogEntry { Name = "A", Action1 = valid.Action1 }));
        Assert.Throws<InvalidDataException>(() => store.Upsert(new CatalogEntry { Id = "a", Action1 = valid.Action1 }));
        Assert.Throws<InvalidDataException>(() => store.Upsert(new CatalogEntry { Id = "a", Name = "A" }));
    }

    [Fact]
    public void Hiding_takes_an_app_off_the_device_catalog_and_leaves_it_in_the_admin_one()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        store.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "a", "name": "A", "action1": { "packageId": "x" } },
                        { "id": "b", "name": "B", "action1": { "packageId": "y" } } ] }
            """));

        Assert.True(store.SetHidden("a", true));

        Assert.Equal(2, store.Entries.Count);
        Assert.Equal("b", Assert.Single(store.VisibleEntries).Id);
        Assert.True(store.Find("a")!.Hidden);

        Assert.True(store.SetHidden("a", false));
        Assert.Equal(2, store.VisibleEntries.Count);
    }

    [Fact]
    public void An_imported_file_that_omits_hidden_says_the_app_is_visible()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        var file = """{ "apps": [ { "id": "a", "name": "A", "action1": { "packageId": "x" } } ] }""";
        store.Import(CatalogStore.Parse(file));
        store.SetHidden("a", true);

        // The uploaded file is the statement of what the catalog should be, so it can unhide as well as
        // hide. Nothing re-imports on its own: seeding stops once the catalog has any app in it.
        store.Import(CatalogStore.Parse(file));

        Assert.False(store.Find("a")!.Hidden);
    }

    [Fact]
    public void A_hidden_app_survives_an_export_and_import()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        store.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "a", "name": "A", "action1": { "packageId": "x" } } ] }
            """));
        store.SetHidden("a", true);

        var fresh = new Database(Path.Combine(test.Root, "second.db"));
        fresh.Migrate();
        var second = new CatalogStore(fresh, "");
        second.Import(CatalogStore.Parse(store.ExportJson()));

        Assert.True(second.Find("a")!.Hidden);
    }

    [Fact]
    public void Search_matches_id_name_publisher_and_category()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        store.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "chrome", "name": "Google Chrome", "publisher": "Google LLC", "category": "Browsers", "action1": { "packageId": "x" } },
                        { "id": "7-zip", "name": "7-Zip", "publisher": "Igor Pavlov", "category": "Utilities", "action1": { "packageId": "y" } } ] }
            """));

        Assert.Equal("chrome", Assert.Single(Listed(store, "google")).Id);
        Assert.Equal("chrome", Assert.Single(Listed(store, "Browsers")).Id);
        Assert.Equal("7-zip", Assert.Single(Listed(store, "pavlov")).Id);
        Assert.Equal(2, Listed(store, "").Count);
        Assert.Empty(Listed(store, "nothing here"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Agent_only_apps_round_trip_without_an_action1_row(bool winget)
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        PackageDefinition definition = winget ? new WingetPackageDefinition("Valve.Steam", "machine") : PackageDefinitionTests.Direct;
        store.Upsert(new CatalogEntry { Id = "agent", Name = "Agent app", Agent = definition });
        var entry = store.Find("agent")!;
        Assert.Equal(definition, entry.Agent);
        Assert.Equal(["agent"], entry.ToPublic().Engines);
        Assert.Equal(winget ? null : (long?)5_000_000_000L, entry.ToPublic().DownloadSizeBytes);
        using var connection = test.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT engine FROM catalog_packages WHERE app_id = 'agent';";
        Assert.Equal("agent", command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM catalog_packages WHERE app_id = 'agent';";
        Assert.Equal(1L, command.ExecuteScalar());

        var fresh = new Database(Path.Combine(test.Root, "second.db"));
        fresh.Migrate();
        var second = new CatalogStore(fresh, "");
        second.Import(CatalogStore.Parse(store.ExportJson()));
        Assert.Equal(store.ExportJson(), second.ExportJson());
        Assert.Equal(definition, second.Find("agent")!.Agent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Replacing_packages_removes_stale_engine_rows(bool import)
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        var entry = new CatalogEntry
        {
            Id = "both",
            Name = "Both",
            Agent = PackageDefinitionTests.Direct,
            Action1 = new Action1PackageRef { PackageId = "action1" },
        };
        store.Upsert(entry);
        Assert.Equal(["action1", "agent"], store.Find("both")!.ToPublic().Engines);
        entry.Agent = null;
        Save();
        Assert.Equal(["action1"], store.Find("both")!.ToPublic().Engines);
        Assert.Null(store.Find("both")!.ToPublic().DownloadSizeBytes);
        entry.Agent = PackageDefinitionTests.Direct;
        entry.Action1 = new Action1PackageRef();
        Save();
        Assert.Equal(["agent"], store.Find("both")!.ToPublic().Engines);

        void Save()
        {
            if (import)
            {
                store.Import([entry]);
            }
            else
            {
                store.Upsert(entry);
            }
        }
    }

    [Fact]
    public void Import_and_upsert_validate_agent_definitions_before_writing()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        var bad = new CatalogEntry { Id = "bad", Name = "Bad", Agent = PackageDefinitionTests.Direct with { Sha256 = "" } };
        Assert.Throws<InvalidDataException>(() => store.Upsert(bad));
        Assert.Throws<InvalidDataException>(() => store.Import([
            new CatalogEntry { Id = "good", Name = "Good", Agent = PackageDefinitionTests.Direct }, bad]));
        Assert.Empty(store.Entries);
    }

    /// <summary>
    /// The table is written by two nearly identical statements, one for an import and one for a single
    /// app off the form, and a column added to one and not the other is dropped on every save through
    /// the other. Each column is set on the way in, changed on the way back, and read again: a writer
    /// that leaves a column out of its ON CONFLICT list fails the second read.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Saving_an_app_again_keeps_every_column_the_form_can_change(bool import)
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        var first = new CatalogEntry
        {
            Id = "steam",
            Name = "Steam",
            Publisher = "Valve",
            Description = "Games.",
            Category = "Games",
            IconUrl = "https://example.invalid/steam.png",
            Featured = true,
            Hidden = true,
            EngineOverride = EngineLabel.Agent,
            Requirements = "A Steam account.",
            UserRemovable = true,
            Match = new MatchRule { NameContains = "Steam" },
            Agent = new WingetPackageDefinition("Valve.Steam", "user"),
        };
        Save(store, first, import);

        var written = store.Find("steam")!;
        Assert.True(written.UserRemovable);
        Assert.Equal(EngineLabel.Agent, written.EngineOverride);
        Assert.Equal("A Steam account.", written.Requirements);

        // The same app again, with only the name changed. Everything else has to survive.
        first.Name = "Steam Client";
        Save(store, first, import);

        var again = store.Find("steam")!;
        Assert.Equal("Steam Client", again.Name);
        Assert.True(again.UserRemovable);
        Assert.True(again.Featured);
        Assert.True(again.Hidden);
        Assert.Equal(EngineLabel.Agent, again.EngineOverride);
        Assert.Equal("A Steam account.", again.Requirements);
        Assert.Equal("Valve", again.Publisher);
        Assert.Equal("Steam", again.Match!.NameContains);

        // And turning each of them off has to take, not only turning them on.
        first.UserRemovable = false;
        first.Featured = false;
        first.Hidden = false;
        first.EngineOverride = null;
        first.Requirements = null;
        Save(store, first, import);

        var off = store.Find("steam")!;
        Assert.False(off.UserRemovable);
        Assert.False(off.Featured);
        Assert.False(off.Hidden);
        Assert.Null(off.EngineOverride);
        Assert.Null(off.Requirements);
    }

    private static void Save(CatalogStore store, CatalogEntry entry, bool import)
    {
        if (import)
        {
            store.Import([entry]);
        }
        else
        {
            store.Upsert(entry);
        }
    }

    private static string CheckedInCatalog
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AppPortal.sln")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return Path.Combine(directory.FullName, "deploy", "config", "catalog.json");
        }
    }

    private static IReadOnlyList<CatalogEntry> Listed(CatalogStore store, string term)
        => store.List(new SearchFilter(term), ListQuery.All).Rows;
}
