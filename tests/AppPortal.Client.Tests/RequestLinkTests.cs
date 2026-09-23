using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

/// <summary>
/// A request an administrator answered with an app, as the person who asked sees it: a way to the
/// card when this PC is offered the app, a plain sentence when it is not, and nothing new otherwise.
/// </summary>
public sealed class RequestLinkTests
{
    [Fact]
    public async Task A_linked_request_offers_Show_in_Apps_which_opens_the_card_and_installs_nothing()
    {
        var api = new Api();
        var model = new MainViewModel(api, new ClientSettings());
        await model.RefreshAsync();
        model.SelectedCategory = "Tools";
        model.SelectedSection = 3;

        var request = Assert.Single(model.Requests);
        Assert.Equal("Added to the catalog as Slack.", request.CatalogAppText);
        Assert.True(request.HasCatalogAppText);
        Assert.True(request.CanShowApp);

        request.ShowAppCommand.Execute(null);

        Assert.Equal(0, model.SelectedSection);
        Assert.Equal("All", model.SelectedCategory);
        Assert.Equal("Slack", model.SearchText);
        Assert.Equal("slack", Assert.Single(model.FilteredApps).App.Id);
        Assert.Equal(0, api.InstallsRequested);
    }

    [Fact]
    public async Task A_linked_app_this_PC_is_not_offered_says_so_without_a_button()
    {
        var api = new Api { Catalog = [Chrome] };
        var model = new MainViewModel(api, new ClientSettings());
        await model.RefreshAsync();

        var request = Assert.Single(model.Requests);
        Assert.Equal("Added to the catalog as Slack, but this PC cannot install it.", request.CatalogAppText);
        Assert.False(request.CanShowApp);
    }

    [Fact]
    public void An_unlinked_request_looks_as_it_did_before()
    {
        var request = new RequestItemViewModel(Linked with { CatalogAppId = null, CatalogAppName = null });

        Assert.Equal("", request.CatalogAppText);
        Assert.False(request.HasCatalogAppText);
        Assert.False(request.CanShowApp);
        Assert.Equal("Approved", request.StatusText);
        Assert.Equal("Here you go.", request.Reason);
    }

    [Fact]
    public void The_card_name_is_what_the_sentence_uses()
    {
        var card = new AppItemViewModel(Slack with { Name = "Slack for Windows" }, _ => Task.CompletedTask);

        var request = new RequestItemViewModel(Linked, card);

        Assert.Equal("Added to the catalog as Slack for Windows.", request.CatalogAppText);
        // No way to show it without somewhere to show it.
        Assert.False(request.CanShowApp);
    }

    [Fact]
    public async Task The_demo_links_its_Notepad_request_to_the_Notepad_card()
    {
        var model = new MainViewModel(new DemoPortalApiClient(), new ClientSettings(), isDemo: true);
        await model.RefreshAsync();

        var request = model.Requests.Single(r => r.Request.CatalogAppId is not null);
        Assert.Equal("notepadpp", request.Request.CatalogAppId);
        Assert.Equal("Added to the catalog as Notepad++.", request.CatalogAppText);
        Assert.True(request.CanShowApp);
    }

    private static readonly CatalogApp Slack = new("slack", "Slack", "Slack Technologies", "Chat.", "Communication", null, false);

    private static readonly CatalogApp Chrome = new("chrome", "Google Chrome", "Google", "A browser.", "Tools", null, false);

    private static readonly AppRequest Linked = new("r1", "Slack, for the support rota", "PC", @"CONTOSO\alee",
        AppRequestStatus.Approved, "Here you go.", DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now,
        CatalogAppId: "SLACK", CatalogAppName: "Slack");

    private sealed class Api : IPortalApiClient
    {
        public IReadOnlyList<CatalogApp> Catalog { get; set; } = [Chrome, Slack];

        public int InstallsRequested { get; private set; }

        public Task<IReadOnlyList<CatalogApp>> GetCatalogAsync(CancellationToken ct) => Task.FromResult(Catalog);
        public Task<DeviceInfo> GetDeviceAsync(CancellationToken ct) => Task.FromResult(new DeviceInfo("PC", "endpoint", "Online", null));
        public Task<IReadOnlyList<InstalledApp>> GetInstalledAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InstalledApp>>([]);
        public Task<IReadOnlyList<InstallRequest>> GetInstallsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InstallRequest>>([]);
        public Task<IReadOnlyList<AppRequest>> GetRequestsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AppRequest>>([Linked]);

        public Task<InstallRequest> RequestInstallAsync(string appId, CancellationToken ct)
        {
            InstallsRequested++;
            throw new NotSupportedException();
        }

        public Task<InstallRequest> RequestUninstallAsync(string appId, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppRequest> CreateRequestAsync(string text, CancellationToken ct) => throw new NotSupportedException();
    }
}
