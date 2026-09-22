using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Client.ViewModels.Admin;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

public sealed class AdminKeysTests
{
    [Fact]
    public async Task Loading_lists_every_key_with_its_status_and_no_secret()
    {
        var page = await LoadedAsync();

        Assert.Equal(4, page.Keys.Count);
        Assert.Null(page.ErrorMessage);
        var active = Row(page, "Head office rollout");
        Assert.True(active.IsActive);
        Assert.Equal("Active", active.StatusText);
        Assert.Equal("ape_7Kq2mXa9…", active.PrefixText);
        Assert.Equal("4 of 50", active.UsesText);
        Assert.True(active.CanRevoke);
        var revoked = Row(page, "Warehouse tablets");
        Assert.False(revoked.IsActive);
        Assert.False(revoked.CanRevoke);
        Assert.Equal("never", revoked.ExpiresText);
        Assert.Equal("2", revoked.UsesText);
        Assert.Equal("Exhausted", Row(page, "Pilot group").StatusText);
    }

    [Fact]
    public async Task A_new_key_is_shown_once_and_nowhere_else()
    {
        var page = await LoadedAsync();

        page.OpenCreateCommand.Execute(null);
        Assert.Equal(EngineLabel.Action1, page.NewEngine?.Value);
        page.NewName = "Branch office";
        page.NewEngine = page.Engines.Single(e => e.Value == "both");
        page.NewExpires = "2030-01-31";
        page.NewMaxUses = "10";
        await page.CreateCommand.ExecuteAsync(null);

        Assert.False(page.IsCreateOpen);
        Assert.Null(page.CreateError);
        var plaintext = page.ShowOnce.Secret!;
        Assert.StartsWith("ape_", plaintext);
        Assert.Contains("Branch office", page.ShowOnce.Heading);
        Assert.DoesNotContain(plaintext, page.Notice);
        var created = Row(page, "Branch office");
        Assert.Equal("both", created.EngineText);
        Assert.Equal("0 of 10", created.UsesText);
        Assert.Equal(KeysViewModel.ParseExpiry("2030-01-31"), created.Key.ExpiresAt);
        Assert.DoesNotContain(page.Keys, k => k.PrefixText.Contains(plaintext) || k.ToString().Contains(plaintext));

        page.ShowOnce.DismissCommand.Execute(null);
        Assert.Null(page.ShowOnce.Secret);
    }

    [Fact]
    public async Task A_date_that_does_not_parse_is_refused_before_the_server_is_asked()
    {
        var page = await LoadedAsync();

        page.OpenCreateCommand.Execute(null);
        page.NewName = "Branch office";
        page.NewExpires = "next week";
        await page.CreateCommand.ExecuteAsync(null);

        Assert.True(page.IsCreateOpen);
        Assert.Equal("'next week' is not a date. Write it as YYYY-MM-DD.", page.CreateError);
        Assert.Equal(4, page.Keys.Count);
        Assert.False(page.ShowOnce.IsOpen);
    }

    [Fact]
    public async Task A_key_the_server_refuses_stays_on_the_form_with_its_reason()
    {
        var page = await LoadedAsync();

        page.OpenCreateCommand.Execute(null);
        await page.CreateCommand.ExecuteAsync(null);

        Assert.True(page.IsCreateOpen);
        Assert.Equal("An enrollment key needs a name.", page.CreateError);
        Assert.False(page.ShowOnce.IsOpen);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("2026-09-22", "2026-09-22T23:59:59.9999999+00:00")]
    [InlineData("2026-09-22T12:00:00Z", "2026-09-22T12:00:00.0000000+00:00")]
    public void An_expiry_date_means_the_end_of_that_day_in_utc(string typed, string? expected)
    {
        var parsed = KeysViewModel.ParseExpiry(typed);

        Assert.Equal(expected, parsed?.ToString("o"));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(" 25 ", 25)]
    public void Maximum_uses_are_a_whole_number_or_nothing(string typed, int? expected)
        => Assert.Equal(expected, KeysViewModel.ParseMaxUses(typed));

    [Fact]
    public void Maximum_uses_that_are_not_a_whole_number_are_refused()
        => Assert.Throws<FormatException>(() => KeysViewModel.ParseMaxUses("-3"));

    [Fact]
    public async Task Revoking_asks_first_and_cancelling_changes_nothing()
    {
        var page = await LoadedAsync();
        var key = Row(page, "Head office rollout");

        page.RequestRevokeCommand.Execute(key);
        Assert.True(page.IsRevokeOpen);
        page.CancelRevokeCommand.Execute(null);

        Assert.False(page.IsRevokeOpen);
        Assert.True(Row(page, "Head office rollout").IsActive);
    }

    [Fact]
    public async Task A_confirmed_revoke_takes_the_key_out_of_use()
    {
        var page = await LoadedAsync();

        page.RequestRevokeCommand.Execute(Row(page, "Head office rollout"));
        await page.ConfirmRevokeCommand.ExecuteAsync(null);

        var revoked = Row(page, "Head office rollout");
        Assert.Equal("Revoked", revoked.StatusText);
        Assert.False(revoked.CanRevoke);
        Assert.Contains("is revoked", page.Notice);
        Assert.False(page.IsRevokeOpen);
    }

    [Fact]
    public async Task A_revoked_key_is_not_offered_for_revoking_again()
    {
        var page = await LoadedAsync();

        page.RequestRevokeCommand.Execute(Row(page, "Warehouse tablets"));

        Assert.False(page.IsRevokeOpen);
    }

    [Fact]
    public async Task Opening_a_key_lists_its_enrollment_attempts()
    {
        var page = await LoadedAsync();

        await page.OpenKeyCommand.ExecuteAsync(Row(page, "Warehouse tablets"));

        Assert.True(page.IsDetailOpen);
        Assert.Equal(3, page.Events.Count);
        var refused = page.Events[0];
        Assert.False(refused.Succeeded);
        Assert.Equal("—", refused.DeviceText);
        Assert.Contains(page.Events, e => e.DeviceText == "removed" && e.Succeeded);
        Assert.Contains(page.Events, e => e.DeviceText == "WAREHOUSE-TAB-1");

        page.BackCommand.Execute(null);
        Assert.True(page.IsListOpen);
        Assert.Empty(page.Events);
    }

    [Fact]
    public async Task A_session_that_ends_under_the_page_signs_out_with_a_notice()
    {
        var session = new AdminSession(new DemoAdminApiClient(), NoAdminSessionStore.Instance, "demo");
        var model = new MainViewModel(null, new ClientSettings(), isDemo: true, session);
        await model.SignInAdminAsync("admin", "demo");
        model.SelectedSection = AdminSections.Keys;
        var page = model.AdminArea!.Keys;

        session.Api.Token = "apa_revoked";
        page.OpenCreateCommand.Execute(null);
        page.NewName = "Too late";
        await page.CreateCommand.ExecuteAsync(null);

        Assert.False(model.IsAdminSignedIn);
        Assert.Equal(AdminApiClient.SessionEndedMessage, model.AdminNotice);
        Assert.False(page.ShowOnce.IsOpen);
    }

    private static EnrollmentKeyRow Row(KeysViewModel page, string name) => page.Keys.Single(k => k.Name == name);

    private static async Task<KeysViewModel> LoadedAsync()
    {
        var api = new DemoAdminApiClient();
        api.Token = (await api.SignInAsync(DemoAdminApiClient.DemoUsername, DemoAdminApiClient.DemoPassword, null, CancellationToken.None)).Token;
        var page = new KeysViewModel(api);
        await page.ActivateAsync();
        return page;
    }
}
