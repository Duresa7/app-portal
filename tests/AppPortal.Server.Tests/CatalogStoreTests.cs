using AppPortal.Server.Catalog;

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
}
