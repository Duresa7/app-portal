using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Client.ViewModels.Admin;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

public sealed class AdminDevicesTests
{
    [Fact]
    public async Task Loading_fills_the_table_with_what_each_device_has()
    {
        var page = await LoadedAsync();

        Assert.Equal(5, page.Devices.Count);
        Assert.Null(page.ErrorMessage);
        var agentOnly = Row(page, "DESIGN-WS-02");
        Assert.True(agentOnly.HasAgent);
        Assert.False(agentOnly.HasAction1);
        var disabled = Row(page, "WAREHOUSE-TAB-1");
        Assert.Equal("Disabled", disabled.StatusText);
        Assert.True(disabled.HasAction1);
        Assert.False(disabled.HasAgent);
        Assert.Equal("—", disabled.AgentVersionText);
        Assert.Equal("Head office rollout", Row(page, "RECEPTION-01").EnrolledWithText);
        Assert.True(page.IsListOpen);
    }

    [Fact]
    public async Task Search_narrows_the_table()
    {
        var page = await LoadedAsync();

        page.Search = "finance";
        await page.SearchDevicesCommand.ExecuteAsync(null);

        Assert.Equal("FINANCE-LT-04", Assert.Single(page.Devices).Name);
    }

    [Fact]
    public async Task Opening_a_device_shows_its_settings_history_and_package_managers()
    {
        var page = await LoadedAsync();

        await page.OpenDeviceCommand.ExecuteAsync(Row(page, "FINANCE-LT-04"));

        Assert.True(page.IsDetailOpen);
        Assert.False(page.IsListOpen);
        Assert.Equal("FINANCE-LT-04", page.EditName);
        Assert.Equal("ep-dev-2", page.EditEndpointId);
        Assert.True(page.EditEnabled);
        Assert.Equal(EngineLabel.Agent, page.EditEnginePreference?.Value);
        // The dashboard's history plus the installs and requests pages' own.
        Assert.Equal(4, page.RecentInstalls.Count);
        Assert.Equal(3, page.RecentRequests.Count);
        Assert.Equal(2, page.Managers.Count);

        await page.BackCommand.ExecuteAsync(null);
        Assert.True(page.IsListOpen);
        Assert.Empty(page.Managers);
        Assert.Empty(page.RecentInstalls);
    }

    [Fact]
    public async Task Saving_renames_and_the_table_shows_it_after_going_back()
    {
        var page = await LoadedAsync();
        await page.OpenDeviceCommand.ExecuteAsync(Row(page, "RECEPTION-01"));

        page.EditName = "  FRONT-DESK-01 ";
        page.EditEnabled = false;
        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Saved.", page.Notice);
        Assert.Null(page.ErrorMessage);
        Assert.Equal("FRONT-DESK-01", page.Selected?.Name);
        Assert.False(page.Selected?.Enabled);
        await page.BackCommand.ExecuteAsync(null);
        Assert.Equal("Disabled", Row(page, "FRONT-DESK-01").StatusText);
        Assert.DoesNotContain(page.Devices, d => d.Name == "RECEPTION-01");
    }

    [Fact]
    public async Task A_refused_save_shows_the_server_message_and_keeps_the_form()
    {
        var page = await LoadedAsync();
        await page.OpenDeviceCommand.ExecuteAsync(Row(page, "RECEPTION-01"));

        page.EditName = "FINANCE-LT-04";
        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Another device is already called 'FINANCE-LT-04'.", page.ErrorMessage);
        Assert.Null(page.Notice);
        Assert.Equal("FINANCE-LT-04", page.EditName);
        Assert.Equal("RECEPTION-01", page.Selected?.Name);
    }

    [Fact]
    public async Task Following_the_server_is_saved_as_no_preference_at_all()
    {
        var device = new AdminDevice("d1", "PC-1", "ep-1", true, true, EngineLabel.Agent, "1.0.0", null, null, null,
            DateTimeOffset.UtcNow, null, 0);
        var handler = new FakeHandler(request => request.Method == HttpMethod.Put
            ? Reply(device with { EnginePreference = null })
            : Reply(new AdminDeviceDetail(device, [], [])));
        var page = new DevicesViewModel(new AdminApiClient("https://portal.example", handler) { Token = "apa_test" });
        await page.OpenDeviceCommand.ExecuteAsync(new DeviceRow(device));

        page.EditEnginePreference = page.EnginePreferences[0];
        page.EditEndpointId = "";
        await page.SaveCommand.ExecuteAsync(null);

        var put = Assert.Single(handler.Bodies, b => b.Method == "PUT");
        Assert.Equal("""{"name":"PC-1","action1EndpointId":"","enabled":true,"enginePreference":null}""", put.Body);
        Assert.Equal("Follow the server", page.EditEnginePreference?.Label);
    }

    [Fact]
    public async Task A_rotated_token_is_shown_once_and_gone_when_the_dialog_closes()
    {
        var page = await LoadedAsync();
        await page.OpenDeviceCommand.ExecuteAsync(Row(page, "RECEPTION-01"));

        page.RequestRotateCommand.Execute(null);
        Assert.True(page.IsRotateOpen);
        await page.ConfirmRotateCommand.ExecuteAsync(null);

        Assert.False(page.IsRotateOpen);
        Assert.True(page.ShowOnce.IsOpen);
        var token = page.ShowOnce.Secret!;
        Assert.StartsWith("apd_", token);
        Assert.DoesNotContain(token, page.Notice);
        Assert.DoesNotContain(token, page.ShowOnce.Heading);

        page.ShowOnce.DismissCommand.Execute(null);
        Assert.False(page.ShowOnce.IsOpen);
        Assert.Null(page.ShowOnce.Secret);
    }

    [Fact]
    public async Task Cancelling_the_rotation_issues_nothing()
    {
        var page = await LoadedAsync();
        await page.OpenDeviceCommand.ExecuteAsync(Row(page, "RECEPTION-01"));

        page.RequestRotateCommand.Execute(null);
        page.CancelRotateCommand.Execute(null);

        Assert.False(page.IsRotateOpen);
        Assert.False(page.ShowOnce.IsOpen);
    }

    [Fact]
    public async Task A_device_with_an_install_running_is_not_removed_and_the_reason_is_shown()
    {
        var page = await LoadedAsync();
        await page.OpenDeviceCommand.ExecuteAsync(Row(page, "FINANCE-LT-04"));

        page.RequestRemoveCommand.Execute(null);
        await page.ConfirmRemoveCommand.ExecuteAsync(null);

        Assert.Contains("install still running", page.ErrorMessage);
        Assert.True(page.IsDetailOpen);
        Assert.False(page.IsRemoveOpen);
    }

    [Fact]
    public async Task Removing_a_device_goes_back_to_a_table_without_it()
    {
        var page = await LoadedAsync();
        // The demo PC itself, because it is the one device with nothing running on it.
        var name = DemoIdentity.MachineName;
        await page.OpenDeviceCommand.ExecuteAsync(Row(page, name));

        page.RequestRemoveCommand.Execute(null);
        await page.ConfirmRemoveCommand.ExecuteAsync(null);

        Assert.True(page.IsListOpen);
        Assert.DoesNotContain(page.Devices, d => d.Name == name);
        Assert.Contains(name, page.Notice);
        Assert.Null(page.ErrorMessage);
    }

    [Fact]
    public async Task Adding_a_device_shows_its_token_once_and_lists_it()
    {
        var page = await LoadedAsync();

        page.OpenAddCommand.Execute(null);
        page.NewName = "LAB-07";
        await page.AddCommand.ExecuteAsync(null);

        Assert.False(page.IsAddOpen);
        Assert.Null(page.AddError);
        Assert.StartsWith("apd_", page.ShowOnce.Secret);
        Assert.Equal("Token for LAB-07", page.ShowOnce.Heading);
        Assert.Contains(page.Devices, d => d.Name == "LAB-07" && d.EnrolledWithText == "added by hand");
    }

    [Fact]
    public async Task Adding_a_name_already_registered_stays_on_the_form_with_the_reason()
    {
        var page = await LoadedAsync();

        page.OpenAddCommand.Execute(null);
        page.NewName = "RECEPTION-01";
        await page.AddCommand.ExecuteAsync(null);

        Assert.True(page.IsAddOpen);
        Assert.Contains("already registered", page.AddError);
        Assert.False(page.ShowOnce.IsOpen);
    }

    [Fact]
    public async Task A_session_that_ends_under_the_page_signs_out_with_a_notice()
    {
        var session = new AdminSession(new DemoAdminApiClient(), NoAdminSessionStore.Instance, "demo");
        var model = new MainViewModel(null, new ClientSettings(), isDemo: true, session);
        await model.SignInAdminAsync("admin", "demo");
        model.SelectedSection = AdminSections.Devices;
        var page = model.AdminArea!.Devices;
        Assert.NotEmpty(page.Devices);

        session.Api.Token = "apa_revoked";
        await page.OpenDeviceCommand.ExecuteAsync(page.Devices[0]);

        Assert.False(model.IsAdminSignedIn);
        Assert.Equal(0, model.SelectedSection);
        Assert.Equal(AdminApiClient.SessionEndedMessage, model.AdminNotice);
    }

    private static DeviceRow Row(DevicesViewModel page, string name) => page.Devices.Single(d => d.Name == name);

    private static async Task<DevicesViewModel> LoadedAsync()
    {
        var api = new DemoAdminApiClient();
        api.Token = (await api.SignInAsync(DemoAdminApiClient.DemoUsername, DemoAdminApiClient.DemoPassword, null, CancellationToken.None)).Token;
        var page = new DevicesViewModel(api);
        await page.ActivateAsync();
        return page;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static HttpResponseMessage Reply<T>(T body)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };

    private sealed record Sent(string Method, string? Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Sent> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(new Sent(request.Method.Method, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return respond(request);
        }
    }
}
