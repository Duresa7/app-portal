using System.Net;

using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Client.ViewModels.Admin;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

public sealed class AdminInstallsTests
{
    [Fact]
    public async Task Opening_the_page_loads_the_newest_installs_and_the_filter_choices()
    {
        var (api, _) = AdminScriptedApi.Create();
        var page = new InstallsViewModel(api);

        await page.ActivateAsync();

        Assert.Null(page.ErrorMessage);
        Assert.False(page.IsBusy);
        Assert.Equal(14, page.Rows.Count);
        Assert.Equal(page.Rows.OrderByDescending(r => r.Install.RequestedAt).Select(r => r.Install.Id), page.Rows.Select(r => r.Install.Id));
        Assert.Equal("Showing 1 to 14 of 14", page.PagerText);
        Assert.False(page.HasOlder);
        Assert.False(page.HasNewer);

        Assert.Equal(FilterOption.Any, page.DeviceOptions[0]);
        Assert.Contains(page.DeviceOptions, o => o.Value == "DESIGN-WS-02");
        Assert.Contains(page.AppOptions, o => o is { Label: "Visual Studio Code", Value: "vscode" });
        Assert.Equal(["Any", "Queued", "Running", "Succeeded", "Failed", "Cancelled"], page.StateOptions.Select(o => o.Label));
    }

    [Fact]
    public async Task Filter_sends_the_web_page_fields_and_starts_from_the_first_page()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new InstallsViewModel(api);
        await page.ActivateAsync();

        page.SelectedDevice = page.DeviceOptions.Single(o => o.Value == "DESIGN-WS-02");
        page.SelectedApp = page.AppOptions.Single(o => o.Value == "vscode");
        page.SelectedState = page.StateOptions.Single(o => o.Label == "Running");
        page.Requester = "  mjones ";
        page.From = DateTime.Today.AddDays(-1);
        page.To = DateTime.Today;
        await page.ApplyFilterCommand.ExecuteAsync(null);

        var args = script.Last(nameof(IAdminApiClient.GetInstallsAsync));
        Assert.Equal(new AdminInstallFilter("DESIGN-WS-02", "vscode", InstallState.Running, "mjones",
            DateOnly.FromDateTime(DateTime.Today.AddDays(-1)), DateOnly.FromDateTime(DateTime.Today)), args[0]);
        Assert.Equal(0, args[1]);
        Assert.Equal(InstallsViewModel.PageSize, args[2]);
        var row = Assert.Single(page.Rows);
        Assert.Equal("Visual Studio Code", row.AppName);
        Assert.Equal("Running 70%", row.StateText);
    }

    [Fact]
    public async Task A_device_is_matched_by_its_whole_name_and_a_requester_by_part_of_it()
    {
        var (api, _) = AdminScriptedApi.Create();
        var page = new InstallsViewModel(api);
        await page.ActivateAsync();

        page.SelectedDevice = page.DeviceOptions.Single(o => o.Value == "RECEPTION-01");
        await page.ApplyFilterCommand.ExecuteAsync(null);
        Assert.NotEmpty(page.Rows);
        Assert.All(page.Rows, r => Assert.Equal("RECEPTION-01", r.DeviceName));

        await page.ClearFiltersCommand.ExecuteAsync(null);
        page.Requester = "tbrown";
        await page.ApplyFilterCommand.ExecuteAsync(null);
        Assert.Equal(3, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.Equal(@"CONTOSO\tbrown", r.RequesterText));

        await page.ClearFiltersCommand.ExecuteAsync(null);
        Assert.Equal(FilterOption.Any, page.SelectedDevice);
        Assert.Equal("", page.Requester);
        Assert.Equal(14, page.Rows.Count);
    }

    [Fact]
    public async Task Older_and_newer_step_through_the_history_a_page_at_a_time()
    {
        var (api, script) = AdminScriptedApi.Create();
        script.On(nameof(IAdminApiClient.GetInstallsAsync), args =>
        {
            var offset = (int)args[1]!;
            var rows = Enumerable.Range(offset, Math.Min(100, 250 - offset)).Select(i => Install("ins-" + i)).ToList();
            return Task.FromResult(new AdminPage<AdminInstall>(rows, offset, 100, offset + rows.Count < 250, 250));
        });
        var page = new InstallsViewModel(api);
        await page.ActivateAsync();

        Assert.Equal("Showing 1 to 100 of 250", page.PagerText);
        Assert.False(page.NewerCommand.CanExecute(null));
        Assert.True(page.OlderCommand.CanExecute(null));

        await page.OlderCommand.ExecuteAsync(null);
        await page.OlderCommand.ExecuteAsync(null);
        Assert.Equal(200, script.Last(nameof(IAdminApiClient.GetInstallsAsync))[1]);
        Assert.Equal("Showing 201 to 250 of 250", page.PagerText);
        Assert.False(page.OlderCommand.CanExecute(null));

        await page.NewerCommand.ExecuteAsync(null);
        Assert.Equal(100, script.Last(nameof(IAdminApiClient.GetInstallsAsync))[1]);
        Assert.Equal("Showing 101 to 200 of 250", page.PagerText);
    }

    [Fact]
    public async Task A_reload_updates_the_rows_in_place_and_keeps_the_flyout_open()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new InstallsViewModel(api);
        await page.ActivateAsync();
        var running = page.Rows.First(r => r.CanStop);
        page.SelectedRow = running;

        // The agent reports the install finished between two polls.
        await script.Demo.CancelInstallAsync(running.Install.Id, CancellationToken.None);
        await page.ReloadAsync();

        Assert.Same(running, page.Rows.Single(r => r.Install.Id == running.Install.Id));
        Assert.Same(running, page.SelectedRow);
        Assert.True(page.IsDetailOpen);
        Assert.Equal("Cancelled", running.StateText);
        Assert.False(running.CanStop);
    }

    [Fact]
    public async Task The_page_polls_while_it_is_shown_and_stops_when_it_is_left()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new InstallsViewModel(api, TimeSpan.FromMilliseconds(20)) { IsSelected = true };

        await page.ActivateAsync();
        Assert.True(page.IsAutoRefreshing);
        await WaitUntil(() => script.Count(nameof(IAdminApiClient.GetInstallsAsync)) >= 3);

        page.IsSelected = false;
        Assert.False(page.IsAutoRefreshing);
        // One poll may already be on its way when the page is left; after that, nothing.
        await Task.Delay(60);
        var after = script.Count(nameof(IAdminApiClient.GetInstallsAsync));
        await Task.Delay(150);
        Assert.Equal(after, script.Count(nameof(IAdminApiClient.GetInstallsAsync)));
    }

    [Fact]
    public async Task Stopping_an_agent_install_asks_first_and_then_shows_it_stopped()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new InstallsViewModel(api);
        await page.ActivateAsync();
        var row = page.Rows.First(r => r.CanStop);
        page.SelectedRow = row;

        page.AskToStopCommand.Execute(null);
        Assert.True(page.IsConfirmingStop);
        Assert.Equal(0, script.Count(nameof(IAdminApiClient.CancelInstallAsync)));

        await page.StopCommand.ExecuteAsync(null);

        Assert.False(page.IsConfirmingStop);
        Assert.Equal(InstallState.Cancelled, row.Install.State);
        Assert.Equal("The install was stopped.", page.Notice);
        Assert.Null(page.ErrorMessage);
    }

    [Fact]
    public async Task An_install_Action1_is_running_has_no_stop_and_says_why()
    {
        var (api, _) = AdminScriptedApi.Create();
        var page = new InstallsViewModel(api);
        await page.ActivateAsync();

        var row = page.Rows.Single(r => r.DeviceName == "WAREHOUSE-TAB-1" && r.IsActive);

        Assert.False(row.CanStop);
        Assert.Equal("via Action1", row.EngineText);
        Assert.Contains("stopped in Action1", row.StopUnavailableText);
        Assert.Equal("auto-0008", row.ReferenceText);
    }

    [Fact]
    public async Task A_refused_stop_leaves_the_row_and_shows_the_server_reason()
    {
        var (api, script) = AdminScriptedApi.Create();
        script.On(nameof(IAdminApiClient.CancelInstallAsync), _ => Task.FromException<AdminInstall>(
            new PortalApiException("Visual Studio Code finished before it could be stopped.", HttpStatusCode.Conflict)));
        var page = new InstallsViewModel(api);
        await page.ActivateAsync();
        var row = page.Rows.First(r => r.CanStop);
        var before = row.Install;
        page.SelectedRow = row;

        page.AskToStopCommand.Execute(null);
        await page.StopCommand.ExecuteAsync(null);

        Assert.Equal("Visual Studio Code finished before it could be stopped.", page.ErrorMessage);
        Assert.Equal(before, row.Install);
        Assert.Null(page.Notice);
    }

    [Fact]
    public async Task Everything_on_this_device_filters_to_the_device_of_the_open_install()
    {
        var (api, _) = AdminScriptedApi.Create();
        var page = new InstallsViewModel(api);
        await page.ActivateAsync();
        page.SelectedRow = page.Rows.First(r => r.DeviceName == "FINANCE-LT-04");

        await page.ShowDeviceInstallsCommand.ExecuteAsync(null);

        Assert.Equal("FINANCE-LT-04", page.SelectedDevice.Value);
        Assert.Null(page.SelectedRow);
        Assert.Equal(4, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.Equal("FINANCE-LT-04", r.DeviceName));
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_reported_on_the_page()
    {
        var (api, script) = AdminScriptedApi.Create();
        script.On(nameof(IAdminApiClient.GetInstallsAsync), _ => Task.FromException<AdminPage<AdminInstall>>(
            new PortalApiException("Cannot reach the App Portal server. No such host is known.")));
        var page = new InstallsViewModel(api);

        await page.ActivateAsync();

        Assert.Equal("Cannot reach the App Portal server. No such host is known.", page.ErrorMessage);
        Assert.False(page.IsBusy);
        Assert.Empty(page.Rows);
        Assert.False(page.HasRows);

        script.Reset(nameof(IAdminApiClient.GetInstallsAsync));
        await page.RefreshCommand.ExecuteAsync(null);
        Assert.Null(page.ErrorMessage);
        Assert.True(page.HasRows);
    }

    [Fact]
    public async Task A_session_refused_on_the_installs_page_signs_out_with_a_notice()
    {
        var session = new AdminSession(new DemoAdminApiClient(), NoAdminSessionStore.Instance, "demo");
        var model = new MainViewModel(null, new ClientSettings(), isDemo: true, session);
        await model.SignInAdminAsync("admin", "demo");
        model.SelectedSection = AdminSections.Installs;
        var page = model.AdminArea!.Installs;
        Assert.True(page.IsAutoRefreshing);

        // Revoked on the web; the next poll is refused.
        session.Api.Token = "apa_revoked";
        await page.ReloadAsync();

        Assert.False(model.IsAdminSignedIn);
        Assert.Equal(AdminApiClient.SessionEndedMessage, model.AdminNotice);
        Assert.False(page.IsAutoRefreshing);
    }

    private static AdminInstall Install(string id) => new(id, "7-zip", "7-Zip", "dev-1", "RECEPTION-01", "ep-dev-1", null, EngineLabel.Agent,
        InstallKind.Install, InstallState.Succeeded, 100, null, null, null, 0, 0, DateTimeOffset.Now, DateTimeOffset.Now, null, null);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not come true in time.");
            await Task.Delay(10);
        }
    }
}
