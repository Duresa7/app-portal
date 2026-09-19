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
