using System.Net;
using System.Text;

using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Client.ViewModels.Admin;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

public sealed class AdminSettingsTests
{
    [Fact]
    public async Task Loading_selects_what_the_server_holds_and_offers_no_save()
    {
        var page = new SettingsViewModel(await SignedInAsync());

        await page.ActivateAsync();

        Assert.Equal(EngineLabel.Agent, page.DefaultEngine?.Value);
        Assert.Equal(EngineLabel.Agent, page.SavedEngine);
        Assert.False(page.SaveCommand.CanExecute(null));
        Assert.Null(page.ErrorMessage);
    }

    [Fact]
    public async Task Saving_a_change_stores_it_on_the_server()
    {
        var api = await SignedInAsync();
        var page = new SettingsViewModel(api);
        await page.ActivateAsync();

        page.DefaultEngine = page.Engines.Single(e => e.Value == EngineLabel.Action1);
        Assert.True(page.SaveCommand.CanExecute(null));
        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal(EngineLabel.Action1, page.SavedEngine);
        Assert.StartsWith("Saved.", page.Notice);
        Assert.False(page.SaveCommand.CanExecute(null));
        Assert.Equal(EngineLabel.Action1, (await api.GetSettingsAsync(CancellationToken.None)).DefaultEngine);
    }

    [Fact]
    public async Task A_refused_save_shows_the_server_message()
    {
        var handler = new FakeHandler(request => request.Method == HttpMethod.Put
            ? Reply(HttpStatusCode.BadRequest, """{"message":"Choose either Action1 or Agent."}""")
            : Reply(HttpStatusCode.OK, """{"defaultEngine":"agent"}"""));
        var page = new SettingsViewModel(new AdminApiClient("https://portal.example", handler) { Token = "apa_test" });
        await page.ActivateAsync();

        page.DefaultEngine = page.Engines.Single(e => e.Value == EngineLabel.Action1);
        await page.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Choose either Action1 or Agent.", page.ErrorMessage);
        Assert.Null(page.Notice);
        Assert.Equal(EngineLabel.Agent, page.SavedEngine);
    }

    [Fact]
    public async Task A_session_that_ends_under_the_page_signs_out_with_a_notice()
    {
        var session = new AdminSession(new DemoAdminApiClient(), NoAdminSessionStore.Instance, "demo");
        var model = new MainViewModel(null, new ClientSettings(), isDemo: true, session);
        await model.SignInAdminAsync("admin", "demo");
        model.SelectedSection = AdminSections.Settings;
        var page = model.AdminArea!.Settings;

        session.Api.Token = "apa_revoked";
        page.DefaultEngine = page.Engines.Single(e => e.Value == EngineLabel.Action1);
        await page.SaveCommand.ExecuteAsync(null);

        Assert.False(model.IsAdminSignedIn);
        Assert.Equal(AdminApiClient.SessionEndedMessage, model.AdminNotice);
    }

    private static async Task<DemoAdminApiClient> SignedInAsync()
    {
        var api = new DemoAdminApiClient();
        api.Token = (await api.SignInAsync(DemoAdminApiClient.DemoUsername, DemoAdminApiClient.DemoPassword, null, CancellationToken.None)).Token;
        return api;
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
