using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Devices;
using AppPortal.Server.Settings;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

/// <summary>
/// The selector as a device actually meets it: which apps it is offered, which engine each would use,
/// and which engine the install it starts is labelled with.
/// </summary>
public sealed class EngineSelectionApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DeviceStore _devices;
    private readonly SettingsStore _settings;

    public EngineSelectionApiTests()
    {
        _devices = new DeviceStore(_test.Database);
        _settings = new SettingsStore(_test.Database);
        using (var connection = _test.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO catalog_apps (id, name, created_at, updated_at) VALUES ('both', 'Both Ways', '', '');
                INSERT INTO catalog_packages (app_id, engine, definition_json)
                VALUES ('both', 'action1', '{"packageId":"pkg-both","version":"latest"}');
                INSERT INTO catalog_packages (app_id, engine, definition_json)
                VALUES ('both', 'agent', '{"kind":"winget","id":"Vendor.Both","scope":"machine"}');

                INSERT INTO catalog_apps (id, name, created_at, updated_at) VALUES ('agentonly', 'Agent Only', '', '');
                INSERT INTO catalog_packages (app_id, engine, definition_json)
                VALUES ('agentonly', 'agent', '{"kind":"winget","id":"Vendor.Only","scope":"machine"}');
                """;
            command.ExecuteNonQuery();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
    }

    [Fact]
    public async Task A_device_without_an_agent_is_not_offered_what_only_an_agent_can_install()
    {
        using var client = Device("ACTION1-PC", endpoint: "endpoint-1", agent: false);

        var catalog = await Catalog(client);

        // Showing a button that can only fail wastes the person's time to no purpose.
        Assert.Equal(["both"], catalog.Select(a => a.Id));
        Assert.Equal("action1", catalog[0].Engine);
    }

    [Fact]
    public async Task A_device_with_only_an_agent_is_offered_both_and_uses_the_agent_for_both()
    {
        using var client = Device("AGENT-PC", endpoint: "", agent: true);

        var catalog = await Catalog(client);

        Assert.Equal(["agentonly", "both"], catalog.Select(a => a.Id).Order());
        Assert.All(catalog, app => Assert.Equal("agent", app.Engine));
    }

    [Fact]
    public async Task The_server_default_decides_for_a_device_that_has_both_and_the_install_says_so()
    {
        using var client = Device("BOTH-PC", endpoint: "endpoint-1", agent: true);

        Assert.Equal("action1", (await Catalog(client)).Single(a => a.Id == "both").Engine);
        var first = await Install(client, "both");
        Assert.Equal("action1", first.Engine);

        _settings.Set(SettingsStore.DefaultEngineKey, "agent");

        Assert.Equal("agent", (await Catalog(client)).Single(a => a.Id == "both").Engine);
        var second = await Install(client, "agentonly");
        Assert.Equal("agent", second.Engine);
    }

    [Fact]
    public async Task An_app_override_beats_the_server_and_a_device_preference_beats_both()
    {
        using var client = Device("BOTH-PC", endpoint: "endpoint-1", agent: true);
        Set("UPDATE catalog_apps SET engine_override = 'agent' WHERE id = 'both';");
        Assert.Equal("agent", (await Catalog(client)).Single(a => a.Id == "both").Engine);

        Set("UPDATE devices SET engine_preference = 'action1' WHERE name = 'BOTH-PC';");
        Assert.Equal("action1", (await Catalog(client)).Single(a => a.Id == "both").Engine);
    }

    [Fact]
    public async Task An_app_no_engine_can_install_is_refused_rather_than_started()
    {
        using var client = Device("ACTION1-PC", endpoint: "endpoint-1", agent: false);

        var response = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("agentonly"));

        // The catalog never offered it, so only an old client or a crafted call arrives here.
        Assert.NotEqual(HttpStatusCode.Accepted, response.StatusCode);
    }

    private async Task<IReadOnlyList<CatalogApp>> Catalog(HttpClient client)
        => await client.GetFromJsonAsync<IReadOnlyList<CatalogApp>>(ApiRoutes.Catalog, Json) ?? [];

    private static async Task<InstallRequest> Install(HttpClient client, string appId)
    {
        var response = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest(appId));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InstallRequest>(Json))!;
    }

    private void Set(string sql)
    {
        using var connection = _test.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private HttpClient Device(string name, string endpoint, bool agent)
    {
        var token = _devices.FindByName(name) is null
            ? _devices.Add(name, endpoint)
            : throw new InvalidOperationException($"{name} already exists.");
        if (agent)
        {
            _devices.RecordHeartbeat(_devices.FindByName(name)!.Id, "0.5.0");
        }

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
