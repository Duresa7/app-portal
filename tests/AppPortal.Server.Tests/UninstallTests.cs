using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Agent;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

/// <summary>
/// Removal matters most for the software that was hardest to put on. Who may do it is the whole of
/// the design: the person who installed something may take it off when the administrator allows it,
/// and nobody may take off what somebody else put on for themselves.
/// </summary>
public sealed class UninstallTests : IDisposable
{
    private const string Ada = @"CONTOSO\ada";
    private const string Bob = @"CONTOSO\bob";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CatalogStore _catalog;
    private readonly InstallStore _installs;
    private readonly string _token;

    public UninstallTests()
    {
        var devices = new DeviceStore(_test.Database);
        _catalog = new CatalogStore(_test.Database, "");
        _installs = new InstallStore(_test.Database);
        _token = devices.Add("AGENT-PC", "");
        devices.RecordHeartbeat(devices.FindByName("AGENT-PC")!.Id, "0.5.0");

        Publish("shared", "A Shared App", removable: true, scope: "machine");
        Publish("mine", "A Personal App", removable: true, scope: "user");
        Publish("locked", "A Locked App", removable: false, scope: "machine");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
    }

    [Fact]
    public async Task Somebody_removes_what_they_installed()
    {
        using var client = Client(Ada);
        await InstallAndFinish(client, "shared");

        var removal = await Remove(client, "shared");

        Assert.Equal(InstallKind.Uninstall, removal.Kind);
        // The same history, the same states: a removal is a thing that happened to this PC too.
        Assert.Equal(2, _installs.All().Count);
    }

    [Fact]
    public async Task An_app_the_administrator_locked_is_refused()
    {
        using var client = Client(Ada);
        await InstallAndFinish(client, "locked");

        var response = await client.PostAsJsonAsync(ApiRoutes.Uninstalls, new CreateUninstallRequest("locked"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("only be removed by an administrator", await Message(response));
    }

    [Fact]
    public async Task Somebody_cannot_remove_what_another_person_installed_for_themselves()
    {
        using var ada = Client(Ada);
        await InstallAndFinish(ada, "mine");

        using var bob = Client(Bob);
        var response = await bob.PostAsJsonAsync(ApiRoutes.Uninstalls, new CreateUninstallRequest("mine"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("installed for somebody else", await Message(response));
    }

    [Fact]
    public async Task A_machine_wide_app_may_be_removed_by_anybody_who_could_have_installed_it()
    {
        using var ada = Client(Ada);
        await InstallAndFinish(ada, "shared");

        // It is on the PC for everyone, so it is not one person's to keep.
        using var bob = Client(Bob);
        var response = await bob.PostAsJsonAsync(ApiRoutes.Uninstalls, new CreateUninstallRequest("shared"));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task Removing_something_that_was_never_installed_is_refused()
    {
        using var client = Client(Ada);

        var response = await client.PostAsJsonAsync(ApiRoutes.Uninstalls, new CreateUninstallRequest("shared"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_app_that_is_not_in_the_catalog_is_not_found()
    {
        using var client = Client(Ada);

        var response = await client.PostAsJsonAsync(ApiRoutes.Uninstalls, new CreateUninstallRequest("absent"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_agent_is_told_it_is_a_removal_rather_than_an_install()
    {
        using var client = Client(Ada);
        await InstallAndFinish(client, "shared");
        await Remove(client, "shared");

        var job = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;

        Assert.Equal(InstallKind.Uninstall, job.Kind);
    }

    [Fact]
    public async Task Two_removals_of_the_same_app_do_not_both_start()
    {
        using var client = Client(Ada);
        await InstallAndFinish(client, "shared");
        await Remove(client, "shared");

        var second = await client.PostAsJsonAsync(ApiRoutes.Uninstalls, new CreateUninstallRequest("shared"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    private void Publish(string id, string name, bool removable, string scope)
        => _catalog.Upsert(new CatalogEntry
        {
            Id = id,
            Name = name,
            UserRemovable = removable,
            Agent = new WingetPackageDefinition($"Vendor.{id}", scope),
        });

    private async Task InstallAndFinish(HttpClient client, string appId)
    {
        var created = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest(appId));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var job = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;
        await client.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/complete", new AgentJobCompletion(true, "Installed.", 0));
    }

    private static async Task<InstallRequest> Remove(HttpClient client, string appId)
    {
        var response = await client.PostAsJsonAsync(ApiRoutes.Uninstalls, new CreateUninstallRequest(appId));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InstallRequest>(Json))!;
    }

    private static async Task<string> Message(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ErrorMessage>())?.Message ?? "";

    private HttpClient Client(string requester)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        client.DefaultRequestHeaders.TryAddWithoutValidation(ApiHeaders.Requester, requester);
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
