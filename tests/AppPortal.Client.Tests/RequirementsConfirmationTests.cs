using AppPortal.Client.ViewModels;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

/// <summary>
/// The one extra click, on the apps that have something to say and on no others. The portal cannot
/// check a requirement, so the only thing it can honestly do is make sure nobody can say they were
/// not told before they waited for a download.
/// </summary>
public sealed class RequirementsConfirmationTests
{
    [Fact]
    public async Task An_app_with_nothing_to_say_installs_on_the_first_press()
    {
        var installs = 0;
        var card = Card(requirements: null, () => installs++);

        await card.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, installs);
        Assert.False(card.IsConfirming);
        Assert.False(card.HasRequirements);
    }

    [Fact]
    public async Task An_app_with_requirements_shows_them_first_and_installs_nothing_yet()
    {
        var installs = 0;
        var card = Card("Needs Secure Boot turned on.", () => installs++);

        await card.InstallCommand.ExecuteAsync(null);

        Assert.Equal(0, installs);
        Assert.True(card.IsConfirming);
        Assert.Equal("Needs Secure Boot turned on.", card.RequirementsText);
        // The Install button is out of the way while the question is on screen, so the two cannot be
        // pressed one after the other by somebody clicking through.
        Assert.False(card.CanInstall);
    }

    [Fact]
    public async Task Agreeing_installs_it()
    {
        var installs = 0;
        var card = Card("Needs a vendor account.", () => installs++);
        await card.InstallCommand.ExecuteAsync(null);

        await card.ConfirmInstallCommand.ExecuteAsync(null);

        Assert.Equal(1, installs);
        Assert.False(card.IsConfirming);
    }

    [Fact]
    public async Task Backing_out_installs_nothing_and_leaves_the_app_where_it_was()
    {
        var installs = 0;
        var card = Card("Windows 11 only.", () => installs++);
        await card.InstallCommand.ExecuteAsync(null);

        card.CancelInstallCommand.Execute(null);

        Assert.Equal(0, installs);
        Assert.False(card.IsConfirming);
        Assert.True(card.CanInstall);
    }

    [Fact]
    public async Task The_question_is_asked_again_the_next_time()
    {
        // Having agreed once is not agreement for ever: the note may have changed, and so may the PC.
        var installs = 0;
        var card = Card("Needs Secure Boot turned on.", () => installs++);
        await card.InstallCommand.ExecuteAsync(null);
        card.CancelInstallCommand.Execute(null);

        await card.InstallCommand.ExecuteAsync(null);

        Assert.True(card.IsConfirming);
        Assert.Equal(0, installs);
    }

    [Fact]
    public void Whitespace_is_not_something_to_say()
    {
        Assert.False(Card("   ", () => { }).HasRequirements);
    }

    private static AppItemViewModel Card(string? requirements, Action onInstall)
    {
        var app = new CatalogApp("app", "An App", "A Vendor", "Does things", "Tools", null, false,
            ["action1"], null, null, "action1", requirements);
        return new AppItemViewModel(app, _ =>
        {
            onInstall();
            return Task.CompletedTask;
        });
    }
}
