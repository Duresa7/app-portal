using AppPortal.Client.ViewModels;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

/// <summary>
/// A game the portal hands to its launcher. What follows the button is a launcher window rather than a
/// progress bar, so the card says so before anybody presses it.
/// </summary>
public sealed class LauncherHandoffCardTests
{
    [Fact]
    public void A_game_says_where_it_opens_and_offers_to_get_it()
    {
        var card = new AppItemViewModel(new CatalogApp("cs2", "Counter-Strike 2", "Valve", "", "Games", null, false)
        {
            InstallScope = "user",
            HandoffTo = "Steam",
        }, _ => Task.CompletedTask);

        Assert.True(card.HasHandoff);
        Assert.Equal("Opens in Steam, where you finish with your own account", card.HandoffText);
        Assert.Equal("Get", card.InstallLabel);
        Assert.True(card.InstallsForYouOnly);
    }

    [Fact]
    public void An_ordinary_app_is_installed_and_says_nothing_about_a_launcher()
    {
        var card = new AppItemViewModel(new CatalogApp("steam", "Steam", "Valve", "", "Games", null, false), _ => Task.CompletedTask);

        Assert.False(card.HasHandoff);
        Assert.Equal("", card.HandoffText);
        Assert.Equal("Install", card.InstallLabel);
    }

    [Fact]
    public void A_server_from_before_handoffs_reads_as_an_ordinary_app()
    {
        var app = System.Text.Json.JsonSerializer.Deserialize<CatalogApp>(
            """{"id":"steam","name":"Steam","publisher":"Valve","description":"","category":"Games","iconUrl":null,"featured":false,"engines":["agent"],"downloadSizeBytes":null}""",
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        Assert.Null(app.HandoffTo);
        Assert.False(new AppItemViewModel(app, _ => Task.CompletedTask).HasHandoff);
    }
}
