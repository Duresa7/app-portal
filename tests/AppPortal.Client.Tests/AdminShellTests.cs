using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Client.ViewModels.Admin;

namespace AppPortal.Client.Tests;

public sealed class AdminShellTests
{
    [Fact]
    public async Task Signing_in_opens_the_admin_area_on_the_dashboard()
    {
        var model = Model(out _);
        Assert.True(model.ShowAdminEntry);

        model.OpenSignInCommand.Execute(null);
        Assert.True(model.IsSignInOpen);
        model.SignInUsername = DemoAdminApiClient.DemoUsername;
        model.SignInPassword = DemoAdminApiClient.DemoPassword;
        await model.SubmitSignInCommand.ExecuteAsync(null);

        Assert.True(model.IsAdminSignedIn);
        Assert.False(model.ShowAdminEntry);
        Assert.False(model.IsSignInOpen);
        Assert.Equal("", model.SignInPassword);
        Assert.Equal("admin", model.AdminUsername);
        Assert.Equal(AdminSections.Dashboard, model.SelectedSection);
        var dashboard = Assert.IsType<DashboardViewModel>(model.CurrentAdminPage);
        Assert.True(dashboard.IsSelected);
        Assert.NotNull(dashboard.Counts);
        Assert.Null(dashboard.ErrorMessage);
    }

    [Fact]
    public async Task A_wrong_password_stays_on_the_dialog_with_the_reason()
    {
        var model = Model(out _);
        model.OpenSignInCommand.Execute(null);
        model.SignInUsername = "admin";
        model.SignInPassword = "guess";

        await model.SubmitSignInCommand.ExecuteAsync(null);

        Assert.False(model.IsAdminSignedIn);
        Assert.True(model.IsSignInOpen);
        Assert.Contains("do not match", model.SignInError);
        Assert.Equal("", model.SignInPassword);
        Assert.Equal("admin", model.SignInUsername);
    }

    [Fact]
    public async Task An_empty_form_is_refused_before_the_server_is_asked()
    {
        var model = Model(out _);

        Assert.False(await model.SignInAdminAsync("admin", ""));

        Assert.Equal("Enter your administrator user name and password.", model.SignInError);
    }

    [Fact]
    public async Task Every_admin_section_has_its_page()
    {
        var model = Model(out _);
        await model.SignInAdminAsync("admin", "demo");

        var pages = new Dictionary<int, Type>
        {
            [AdminSections.Dashboard] = typeof(DashboardViewModel),
            [AdminSections.Installs] = typeof(InstallsViewModel),
            [AdminSections.Catalog] = typeof(CatalogViewModel),
            [AdminSections.Requests] = typeof(RequestsViewModel),
            [AdminSections.Devices] = typeof(DevicesViewModel),
            [AdminSections.Keys] = typeof(KeysViewModel),
            [AdminSections.Admins] = typeof(AdminsViewModel),
            [AdminSections.Settings] = typeof(SettingsViewModel),
        };
        foreach (var (section, type) in pages)
        {
            model.SelectedSection = section;
            Assert.IsType(type, model.CurrentAdminPage);
            Assert.True(model.ShowAdmin);
            Assert.False(model.ShowApps);
            Assert.Single(model.AdminArea!.Pages, p => p.IsSelected);
        }

        model.SelectedSection = 1;
        Assert.Null(model.CurrentAdminPage);
        Assert.DoesNotContain(model.AdminArea!.Pages, p => p.IsSelected);
    }

    [Fact]
    public async Task A_dashboard_tile_opens_the_page_behind_it()
    {
        var model = Model(out _);
        await model.SignInAdminAsync("admin", "demo");

        model.AdminArea!.Dashboard.OpenRequestsCommand.Execute(null);

        Assert.Equal(AdminSections.Requests, model.SelectedSection);
        Assert.True(model.AdminArea.Requests.IsSelected);
    }

    [Fact]
    public async Task Signing_out_closes_the_admin_area_without_a_notice()
    {
        var model = Model(out var session);
        await model.SignInAdminAsync("admin", "demo");
        model.SelectedSection = AdminSections.Catalog;

        await model.SignOutCommand.ExecuteAsync(null);

        Assert.False(model.IsAdminSignedIn);
        Assert.False(session.IsSignedIn);
        Assert.True(model.ShowAdminEntry);
        Assert.Equal(0, model.SelectedSection);
        Assert.Null(model.AdminNotice);
        Assert.False(model.IsSignInOpen);
    }

    [Fact]
    public async Task A_session_that_ends_under_a_page_signs_out_with_a_notice()
    {
        var model = Model(out var session);
        await model.SignInAdminAsync("admin", "demo");
        model.SelectedSection = AdminSections.Installs;

        // The web revoked it: the next call anywhere is refused.
        session.Api.Token = "apa_revoked";
        model.SelectedSection = AdminSections.Dashboard;

        Assert.False(model.IsAdminSignedIn);
        Assert.Equal(0, model.SelectedSection);
        Assert.Equal(AdminApiClient.SessionEndedMessage, model.AdminNotice);
        Assert.False(model.IsSignInOpen);

        model.OpenSignInCommand.Execute(null);
        await model.SignInAdminAsync("admin", "demo");
        Assert.Null(model.AdminNotice);
    }

    [Fact]
    public async Task A_kept_session_opens_the_admin_area_at_start()
    {
        var signedIn = await new DemoAdminApiClient().SignInAsync("admin", "demo", null, CancellationToken.None);
        var store = new KeptStore(new StoredAdminSession("demo", "admin", signedIn.Token));

        var model = new MainViewModel(null, new ClientSettings(), isDemo: false, new AdminSession(new DemoAdminApiClient(), store, "demo"));

        Assert.True(model.IsAdminSignedIn);
        Assert.Equal("admin", model.AdminUsername);
        Assert.Equal(0, model.SelectedSection);
    }

    [Fact]
    public void An_admin_page_asked_for_while_signed_out_asks_for_the_sign_in()
    {
        var model = Model(out _);

        model.SelectedSection = AdminSections.Requests;
        Assert.True(model.IsSignInOpen);
        Assert.False(model.ShowAdmin);

        model.CancelSignInCommand.Execute(null);
        Assert.False(model.IsSignInOpen);
        Assert.Equal(0, model.SelectedSection);
    }

    [Fact]
    public void Without_a_server_there_is_no_way_in()
    {
        var model = new MainViewModel(null, new ClientSettings());

        Assert.False(model.CanAdminister);
        Assert.False(model.ShowAdminEntry);
        model.SelectedSection = AdminSections.Dashboard;
        Assert.False(model.IsSignInOpen);
        Assert.False(model.ShowAdmin);
    }

    [Fact]
    public async Task Approve_and_add_lands_on_the_catalog_with_the_editor_open()
    {
        var model = Model(out _);
        await model.SignInAdminAsync("admin", "demo");
        model.SelectedSection = AdminSections.Requests;
        var requests = model.AdminArea!.Requests;
        await WaitUntil(() => !requests.IsBusy && requests.Rows.Count > 0);
        var row = requests.Rows.Single(r => r.Request.Id == "req-1");

        requests.ApproveCommand.Execute(row);
        await requests.ApproveAndAddCommand.ExecuteAsync(null);

        Assert.Equal(AdminSections.Catalog, model.SelectedSection);
        var catalog = Assert.IsType<CatalogViewModel>(model.CurrentAdminPage);
        var editor = Assert.IsType<CatalogEditorViewModel>(catalog.Editor);
        Assert.Equal("req-1", editor.FromRequest!.Id);
        Assert.Equal("Slack", editor.Name);
        Assert.Equal("slack", editor.Id);
        Assert.False(editor.IsDirty);

        // The list under the form is read even though the page was never opened before.
        await WaitUntil(() => !catalog.IsBusy && catalog.Apps.Count > 0);
        Assert.Same(editor, catalog.Editor);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not come true in time.");
            await Task.Delay(10);
        }
    }

    private static MainViewModel Model(out AdminSession session)
    {
        session = new AdminSession(new DemoAdminApiClient(), NoAdminSessionStore.Instance, "demo");
        return new MainViewModel(null, new ClientSettings(), isDemo: true, session);
    }

    private sealed class KeptStore(StoredAdminSession kept) : IAdminSessionStore
    {
        public StoredAdminSession? Load() => kept;

        public void Save(StoredAdminSession session)
        {
        }

        public void Clear()
        {
        }
    }
}
