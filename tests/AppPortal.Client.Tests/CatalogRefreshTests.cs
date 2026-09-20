using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

public sealed class CatalogRefreshTests
{
    [Fact]
    public async Task Refresh_updates_existing_cards_and_filters_without_losing_install_state()
    {
        var api = new CatalogApi();
        var model = new MainViewModel(api, new ClientSettings());
        await model.RefreshAsync();
        var card = Assert.Single(model.Apps);
        card.IsRequesting = true;
        card.LastError = "Retained status";
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        model.SelectedCategory = "Tools";
        api.Catalog = [api.Catalog[0] with
        {
            Name = "Renamed app", Publisher = "New publisher", Description = "Updated description",
            Category = "Design", Featured = true,
        }];

        await model.RefreshAsync();

        Assert.Same(card, Assert.Single(model.Apps));
        Assert.Equal("Renamed app", card.Name);
        Assert.Equal("New publisher", card.Publisher);
        Assert.Equal("Updated description", card.Description);
        Assert.Equal("Design", card.Category);
        Assert.True(card.Featured);
        Assert.Equal("R", card.Initial);
        Assert.True(card.IsRequesting);
        Assert.Equal("Retained status", card.LastError);
        Assert.Contains(nameof(card.Name), changed);
        Assert.Contains(nameof(card.Initial), changed);
        Assert.Equal("All", model.SelectedCategory);
        Assert.Contains("Design", model.Categories);
        Assert.DoesNotContain("Tools", model.Categories);
        model.SearchText = "Renamed";
        Assert.Same(card, Assert.Single(model.FilteredApps));
        model.SearchText = "Original";
        Assert.Empty(model.FilteredApps);
    }

    [Fact]
    public async Task Refresh_removes_hidden_apps_and_adds_new_apps()
    {
        var api = new CatalogApi();
        var model = new MainViewModel(api, new ClientSettings());
        await model.RefreshAsync();
        api.Catalog = [new("new", "New app", "Publisher", "Description", "Design", null, false)];
        await model.RefreshAsync();
        Assert.Equal("new", Assert.Single(model.Apps).App.Id);
        Assert.Equal("new", Assert.Single(model.FilteredApps).App.Id);
    }

    private sealed class CatalogApi : IPortalApiClient
    {
        public IReadOnlyList<CatalogApp> Catalog { get; set; } =
            [new("app", "Original app", "Publisher", "Description", "Tools", null, false)];
        public Task<IReadOnlyList<CatalogApp>> GetCatalogAsync(CancellationToken ct) => Task.FromResult(Catalog);
        public Task<DeviceInfo> GetDeviceAsync(CancellationToken ct) => Task.FromResult(new DeviceInfo("PC", "endpoint", "Online", null));
        public Task<IReadOnlyList<InstalledApp>> GetInstalledAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InstalledApp>>([]);
        public Task<IReadOnlyList<InstallRequest>> GetInstallsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<InstallRequest>>([]);
        public Task<IReadOnlyList<AppRequest>> GetRequestsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AppRequest>>([]);
        public Task<InstallRequest> RequestInstallAsync(string appId, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppRequest> CreateRequestAsync(string text, CancellationToken ct) => throw new NotSupportedException();
    }
}
