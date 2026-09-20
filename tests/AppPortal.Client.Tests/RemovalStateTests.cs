using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

/// <summary>
/// A removal is a row in the same history as an install, with the same states and the same progress.
/// That is what makes it cheap on the server and what makes it easy for the client to say the wrong
/// thing: "Succeeded" on a removal is not "Installed", and the card has to go back to offering Install.
/// </summary>
public sealed class RemovalStateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task An_app_that_was_just_removed_is_offered_again()
    {
        var api = new Api
        {
            // The inventory sweep has not caught up yet, which is the ordinary case straight after a
            // removal: the history is what knows.
            Installed = [new InstalledApp("An App", "A Vendor", "1.0", "app")],
            Installs =
            [
                Row(InstallKind.Uninstall, InstallState.Succeeded, Now.AddMinutes(-1)),
                Row(InstallKind.Install, InstallState.Succeeded, Now.AddHours(-2)),
            ],
        };
        var model = new MainViewModel(api, new ClientSettings());

        await model.RefreshAsync();

        var card = Assert.Single(model.Apps);
        Assert.False(card.IsInstalled);
        Assert.True(card.CanInstall);
        Assert.False(card.CanRemove);
        Assert.Equal("Not installed", card.StatusText);
    }

    [Fact]
    public async Task Putting_it_back_on_after_a_removal_counts()
    {
        var api = new Api
        {
            Installs =
            [
                Row(InstallKind.Install, InstallState.Succeeded, Now.AddMinutes(-1)),
                Row(InstallKind.Uninstall, InstallState.Succeeded, Now.AddHours(-2)),
            ],
        };
        var model = new MainViewModel(api, new ClientSettings());

        await model.RefreshAsync();

        var card = Assert.Single(model.Apps);
        Assert.True(card.IsInstalled);
        Assert.True(card.CanRemove);
    }

    [Fact]
    public async Task A_removal_still_running_says_so_rather_than_saying_it_is_installing()
    {
        var api = new Api
        {
            Installed = [new InstalledApp("An App", "A Vendor", "1.0", "app")],
            Installs = [Row(InstallKind.Uninstall, InstallState.Running, null, percent: 40)],
        };
        var model = new MainViewModel(api, new ClientSettings());

        await model.RefreshAsync();

        var card = Assert.Single(model.Apps);
        Assert.Equal("Removing 40%", card.StatusText);
        // And it is not offered a second time while the first one is running.
        Assert.False(card.CanRemove);
        Assert.False(card.CanInstall);
    }

    [Theory]
    [InlineData(InstallKind.Install, InstallState.Succeeded, "Installed")]
    [InlineData(InstallKind.Uninstall, InstallState.Succeeded, "Removed")]
    [InlineData(InstallKind.Install, InstallState.Running, "Installing 40%")]
    [InlineData(InstallKind.Uninstall, InstallState.Running, "Removing 40%")]
    public void History_says_which_of_the_two_happened(string kind, InstallState state, string expected)
    {
        var row = new ActivityItemViewModel(Row(kind, state, state == InstallState.Succeeded ? Now : null, percent: 40));

        Assert.Equal(expected, row.StateText);
    }

    [Fact]
    public void Starting_a_removal_takes_the_button_away_at_once()
    {
        var app = new CatalogApp("app", "An App", "A Vendor", "Does things", "Tools", null, false,
            ["agent"], null, null, EngineLabel.Agent, null, UserRemovable: true);
        var card = new AppItemViewModel(app, _ => Task.CompletedTask, _ => Task.CompletedTask) { IsInstalled = true };
        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Assert.True(card.CanRemove);

        card.ActiveInstall = Row(InstallKind.Uninstall, InstallState.Queued, null);

        Assert.False(card.CanRemove);
        Assert.False(card.RemoveCommand.CanExecute(null));
        Assert.Contains(nameof(card.CanRemove), changed);
    }

    private static InstallRequest Row(string kind, InstallState state, DateTimeOffset? completed, int percent = 0)
        => new(Guid.NewGuid().ToString("N"), "app", "An App", "PC", Now.AddHours(-3), completed, state, percent,
            null, null, EngineLabel.Agent, null, null, 0, 0, kind);

    private sealed class Api : IPortalApiClient
    {
        public IReadOnlyList<CatalogApp> Catalog { get; set; } =
        [
            new("app", "An App", "A Vendor", "Does things", "Tools", null, false,
                ["agent"], null, null, EngineLabel.Agent, null, UserRemovable: true),
        ];

        public IReadOnlyList<InstalledApp> Installed { get; set; } = [];

        public IReadOnlyList<InstallRequest> Installs { get; set; } = [];

        public Task<IReadOnlyList<CatalogApp>> GetCatalogAsync(CancellationToken ct) => Task.FromResult(Catalog);
        public Task<DeviceInfo> GetDeviceAsync(CancellationToken ct) => Task.FromResult(new DeviceInfo("PC", "endpoint", "Online", null));
        public Task<IReadOnlyList<InstalledApp>> GetInstalledAsync(CancellationToken ct) => Task.FromResult(Installed);
        public Task<IReadOnlyList<InstallRequest>> GetInstallsAsync(CancellationToken ct) => Task.FromResult(Installs);
        public Task<IReadOnlyList<AppRequest>> GetRequestsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<AppRequest>>([]);
        public Task<InstallRequest> RequestInstallAsync(string appId, CancellationToken ct) => throw new NotSupportedException();
        public Task<InstallRequest> RequestUninstallAsync(string appId, CancellationToken ct) => throw new NotSupportedException();
        public Task<AppRequest> CreateRequestAsync(string text, CancellationToken ct) => throw new NotSupportedException();
    }
}
