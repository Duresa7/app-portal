using AppPortal.Server.Catalog;
using AppPortal.Server.Data;

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

        Assert.Equal("chrome", Assert.Single(store.Search("google")).Id);
        Assert.Equal("chrome", Assert.Single(store.Search("Browsers")).Id);
        Assert.Equal("7-zip", Assert.Single(store.Search("pavlov")).Id);
        Assert.Equal(2, store.Search("").Count);
        Assert.Empty(store.Search("nothing here"));
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
}
