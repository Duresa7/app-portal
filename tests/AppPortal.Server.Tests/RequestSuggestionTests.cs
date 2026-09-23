using AppPortal.Shared;

namespace AppPortal.Server.Tests;

public sealed class RequestSuggestionTests
{
    [Theory]
    [InlineData("Slack, for the new support rota", "Slack", "slack")]
    [InlineData("Notepad++", "Notepad++", "notepad")]
    [InlineData("Node.js please", "Node.js please", "node-js-please")]
    [InlineData("Blender. For the product renders", "Blender", "blender")]
    [InlineData("7-Zip", "7-Zip", "7-zip")]
    [InlineData("Café Studio (for menus)", "Café Studio", "cafe-studio")]
    [InlineData("new", "new", "")]
    [InlineData("Figma\nfor design", "Figma", "figma")]
    [InlineData("Figma\r\nfor design", "Figma", "figma")]
    [InlineData("Paint.NET - for screenshots", "Paint.NET", "paint-net")]
    [InlineData("Zoom; the desktop one", "Zoom", "zoom")]
    [InlineData("Git: the command line", "Git", "git")]
    [InlineData("VLC!", "VLC", "vlc")]
    [InlineData("Is Inkscape allowed?", "Is Inkscape allowed", "is-inkscape-allowed")]
    [InlineData("Postman.", "Postman", "postman")]
    [InlineData("export", "export", "")]
    [InlineData("   ", "", "")]
    [InlineData("", "", "")]
    public void Suggests_a_name_and_an_id_from_the_request(string text, string name, string id)
    {
        Assert.Equal(name, RequestSuggestion.Name(text));
        Assert.Equal(id, RequestSuggestion.Id(RequestSuggestion.Name(text)));
    }

    [Fact]
    public void Null_suggests_nothing()
    {
        Assert.Equal("", RequestSuggestion.Name(null));
        Assert.Equal("", RequestSuggestion.Id(null));
    }

    [Fact]
    public void A_long_name_is_cut_at_the_last_space_within_the_limit()
    {
        // 78 letters, a space, then a word that runs past 80.
        var text = new string('a', 78) + " " + "bbbbbbbbbb";

        Assert.Equal(new string('a', 78), RequestSuggestion.Name(text));
    }

    [Fact]
    public void A_space_exactly_at_the_limit_is_where_the_name_is_cut()
    {
        var text = new string('a', RequestSuggestion.MaxNameLength) + " more words";

        Assert.Equal(new string('a', RequestSuggestion.MaxNameLength), RequestSuggestion.Name(text));
    }

    [Fact]
    public void A_long_name_without_a_space_is_cut_at_the_limit()
    {
        var text = new string('a', 100);

        Assert.Equal(new string('a', RequestSuggestion.MaxNameLength), RequestSuggestion.Name(text));
    }

    [Fact]
    public void A_long_id_is_cut_to_the_limit_without_a_trailing_dash()
    {
        // The 64th character would be the dash between the two words.
        var name = new string('a', 63) + " bbbb";

        var id = RequestSuggestion.Id(name);

        Assert.Equal(new string('a', 63), id);
        Assert.True(id.Length <= RequestSuggestion.MaxIdLength);
    }

    [Fact]
    public void An_id_of_exactly_the_limit_is_kept_whole()
    {
        var name = new string('x', RequestSuggestion.MaxIdLength + 10);

        Assert.Equal(new string('x', RequestSuggestion.MaxIdLength), RequestSuggestion.Id(name));
    }
}
