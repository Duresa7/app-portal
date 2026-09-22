using System.Net;
using System.Text;

using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Client.ViewModels.Admin;

namespace AppPortal.Client.Tests;

public sealed class AdminAccountsTests
{
    [Fact]
    public async Task Loading_marks_your_own_account_and_the_directory_ones()
    {
        var page = await LoadedAsync();

        Assert.Equal(4, page.Accounts.Count);
        Assert.Null(page.ErrorMessage);
        var you = Row(page, "admin");
        Assert.True(you.IsYou);
        Assert.False(you.CanDisable);
        Assert.True(you.CanResetPassword);
        var directory = Row(page, @"CONTOSO\jsmith");
        Assert.Equal("Directory", directory.PasswordText);
        Assert.False(directory.CanResetPassword);
        Assert.True(directory.CanDisable);
        var disabled = Row(page, "contractor");
        Assert.Equal("Disabled", disabled.StatusText);
        Assert.False(disabled.CanDisable);
    }

    [Fact]
    public async Task You_cannot_disable_your_own_account()
    {
        var page = await LoadedAsync();

        page.RequestDisableCommand.Execute(Row(page, "admin"));

        Assert.False(page.IsDisableOpen);
        Assert.Equal("You cannot disable the account you are signed in with.", page.ErrorMessage);
        await page.RefreshCommand.ExecuteAsync(null);
        Assert.Equal("Enabled", Row(page, "admin").StatusText);
    }

    [Fact]
    public async Task The_server_refusal_is_shown_when_the_page_does_not_know_who_you_are()
    {
        // No user name reaches the page, so only the server stands between you and your own account.
        var page = await LoadedAsync(currentUsername: null);
        Assert.DoesNotContain(page.Accounts, a => a.IsYou);

        page.RequestDisableCommand.Execute(Row(page, "admin"));
        await page.ConfirmDisableCommand.ExecuteAsync(null);

        Assert.Equal("You cannot disable the account you are signed in with.", page.ErrorMessage);
        Assert.Null(page.Notice);
    }

    [Fact]
    public async Task Disabling_another_administrator_asks_first()
    {
        var page = await LoadedAsync();

        page.RequestDisableCommand.Execute(Row(page, "helpdesk"));
        Assert.True(page.IsDisableOpen);
        await page.ConfirmDisableCommand.ExecuteAsync(null);

        Assert.False(page.IsDisableOpen);
        Assert.Equal("Disabled", Row(page, "helpdesk").StatusText);
        Assert.Contains("disabled and signed out", page.Notice);
    }

    [Fact]
    public async Task A_generated_password_is_shown_once_for_a_new_administrator()
    {
        var page = await LoadedAsync();

        page.OpenAddCommand.Execute(null);
        Assert.True(page.GenerateNewPassword);
        page.NewUsername = " deskside ";
        await page.AddCommand.ExecuteAsync(null);

        Assert.False(page.IsAddOpen);
        Assert.Null(page.AddError);
        Assert.Contains(page.Accounts, a => a.Username == "deskside");
        var password = page.ShowOnce.Secret!;
        Assert.Equal(20, password.Length);
        Assert.DoesNotContain(password, page.Notice);
        Assert.Equal("", page.NewPassword);

        page.ShowOnce.DismissCommand.Execute(null);
        Assert.Null(page.ShowOnce.Secret);
    }

    [Fact]
    public async Task A_typed_password_goes_to_the_server_once_and_leaves_the_form()
    {
        var handler = new FakeHandler(request => request.Method == HttpMethod.Post
            ? Reply(HttpStatusCode.Created, """{"id":"a9","username":"deskside","disabled":false,"source":"local","createdAt":"2026-09-22T00:00:00Z","lastLoginAt":null}""")
            : Reply(HttpStatusCode.OK, """{"items":[],"offset":0,"limit":50,"hasMore":false,"total":0}"""));
        var page = new AdminsViewModel(new AdminApiClient("https://portal.example", handler) { Token = "apa_test" }, "admin");

        page.OpenAddCommand.Execute(null);
        page.GenerateNewPassword = false;
        page.NewUsername = "deskside";
        page.NewPassword = "correct horse battery";
        await page.AddCommand.ExecuteAsync(null);

        var post = Assert.Single(handler.Sent, s => s.Method == "POST");
        Assert.Equal("""{"username":"deskside","password":"correct horse battery"}""", post.Body);
        Assert.Equal("", page.NewPassword);
        Assert.False(page.ShowOnce.IsOpen);
        Assert.Equal("Administrator 'deskside' added.", page.Notice);
    }

    [Fact]
    public async Task A_short_password_is_refused_before_the_server_is_asked()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.OK, """{"items":[],"offset":0,"limit":50,"hasMore":false,"total":0}"""));
        var page = new AdminsViewModel(new AdminApiClient("https://portal.example", handler) { Token = "apa_test" }, "admin");

        page.OpenAddCommand.Execute(null);
        page.GenerateNewPassword = false;
        page.NewUsername = "deskside";
        page.NewPassword = "short";
        await page.AddCommand.ExecuteAsync(null);

        Assert.True(page.IsAddOpen);
        Assert.Equal("The password must be at least 12 characters.", page.AddError);
        Assert.Equal("", page.NewPassword);
        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Resetting_your_own_password_warns_and_asks_the_server_nothing_more()
    {
        // After the reset the server refuses this session. Reading the table again would sign out before
        // the new password had been read, so the page must not call again by itself.
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.NoContent, ""));
        var page = new AdminsViewModel(new AdminApiClient("https://portal.example", handler) { Token = "apa_test" }, "admin");
        var you = new AdminAccountRow(new AppPortal.Shared.AdminAccount("a1", "admin", false, "local", DateTimeOffset.UtcNow, null), IsYou: true);

        page.OpenResetCommand.Execute(you);
        Assert.True(page.IsResetOpen);
        Assert.True(page.ResetIsYou);
        await page.ResetCommand.ExecuteAsync(null);

        var only = Assert.Single(handler.Sent);
        Assert.Equal("POST", only.Method);
        Assert.StartsWith("""{"password":""", only.Body);
        Assert.False(page.IsResetOpen);
        Assert.True(page.ShowOnce.IsOpen);
        Assert.Contains(page.ShowOnce.Secret!, only.Body);
        Assert.Contains("sign in again", page.Notice);
    }

    [Fact]
    public async Task A_refused_reset_shows_the_server_message_on_the_form()
    {
        var handler = new FakeHandler(_ => Reply(HttpStatusCode.BadRequest,
            """{"message":"'CONTOSO\\jsmith' signs in through the directory, so its password is not kept here. Change it in the directory."}"""));
        var page = new AdminsViewModel(new AdminApiClient("https://portal.example", handler) { Token = "apa_test" }, "admin");
        var other = new AdminAccountRow(new AppPortal.Shared.AdminAccount("a4", @"CONTOSO\jsmith", false, "local", DateTimeOffset.UtcNow, null), IsYou: false);

        page.OpenResetCommand.Execute(other);
        page.GenerateResetPassword = false;
        page.ResetPassword = "a long enough password";
        await page.ResetCommand.ExecuteAsync(null);

        Assert.True(page.IsResetOpen);
        Assert.StartsWith(@"'CONTOSO\jsmith' signs in through the directory", page.ResetError);
        Assert.Equal("", page.ResetPassword);
        Assert.False(page.ShowOnce.IsOpen);
    }

    [Fact]
    public async Task The_signed_in_administrator_reaches_the_page_through_the_shell()
    {
        var session = new AdminSession(new DemoAdminApiClient(), NoAdminSessionStore.Instance, "demo");
        var model = new MainViewModel(null, new ClientSettings(), isDemo: true, session);
        await model.SignInAdminAsync("ADMIN", "demo");

        model.SelectedSection = AdminSections.Admins;

        Assert.True(Row(model.AdminArea!.Admins, "admin").IsYou);
    }

    [Fact]
    public async Task A_session_that_ends_under_the_page_signs_out_with_a_notice()
    {
        var session = new AdminSession(new DemoAdminApiClient(), NoAdminSessionStore.Instance, "demo");
        var model = new MainViewModel(null, new ClientSettings(), isDemo: true, session);
        await model.SignInAdminAsync("admin", "demo");
        model.SelectedSection = AdminSections.Admins;
        var page = model.AdminArea!.Admins;

        session.Api.Token = "apa_revoked";
        page.RequestDisableCommand.Execute(Row(page, "helpdesk"));
        await page.ConfirmDisableCommand.ExecuteAsync(null);

        Assert.False(model.IsAdminSignedIn);
        Assert.Equal(AdminApiClient.SessionEndedMessage, model.AdminNotice);
    }

    private static AdminAccountRow Row(AdminsViewModel page, string username) => page.Accounts.Single(a => a.Username == username);

    private static async Task<AdminsViewModel> LoadedAsync(string? currentUsername = DemoAdminApiClient.DemoUsername)
    {
        var api = new DemoAdminApiClient();
        api.Token = (await api.SignInAsync(DemoAdminApiClient.DemoUsername, DemoAdminApiClient.DemoPassword, null, CancellationToken.None)).Token;
        var page = new AdminsViewModel(api, currentUsername);
        await page.ActivateAsync();
        return page;
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed record Request(string Method, string? Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Request> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(new Request(request.Method.Method, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
