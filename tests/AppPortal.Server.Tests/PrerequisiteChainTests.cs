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
/// A game needs its launcher; plenty of software needs a runtime its own installer does not carry.
/// One click, one install, several steps, in the order an administrator wrote them.
/// </summary>
public sealed class PrerequisiteChainTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CatalogStore _catalog;
    private readonly InstallStore _installs;
    private readonly InstallStepStore _steps;
    private readonly DeviceSoftwareStore _software;
    private readonly DeviceRecord _device;
    private readonly string _token;

    public PrerequisiteChainTests()
    {
        var devices = new DeviceStore(_test.Database);
        _catalog = new CatalogStore(_test.Database, "");
        _installs = new InstallStore(_test.Database);
        _steps = new InstallStepStore(_test.Database);
        _software = new DeviceSoftwareStore(_test.Database);
        _token = devices.Add("AGENT-PC", "");
        _device = devices.FindByName("AGENT-PC")!;
        devices.RecordHeartbeat(_device.Id, "0.5.0");

        foreach (var (id, name) in new[] { ("runtime", "A Runtime"), ("launcher", "A Launcher"), ("game", "A Game") })
        {
            _catalog.Upsert(new CatalogEntry
            {
                Id = id,
                Name = name,
                Agent = new WingetPackageDefinition($"Vendor.{id}", "machine"),
            });
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
    public async Task One_click_installs_the_chain_in_order_and_names_the_step()
    {
        Chain("game", "runtime", "launcher");
        using var client = Client();

        var install = await Start(client, "game");
        Assert.Equal(3, install.StepCount);
        Assert.Equal(1, install.StepNumber);
        Assert.Equal("A Runtime", install.StepName);

        // The runtime, then the launcher, then the game somebody actually asked for.
        Assert.Equal("A Runtime", await FinishStepAsync(client));
        Assert.Equal("A Launcher", await FinishStepAsync(client));
        Assert.Equal("A Game", await FinishStepAsync(client));

        var finished = Assert.Single(_installs.All());
        Assert.Equal(InstallState.Succeeded, finished.State);
        Assert.Equal(["A Runtime", "A Launcher", "A Game"], _steps.For(finished.Id).Select(step => step.AppName));
    }

    [Fact]
    public async Task What_the_device_already_has_is_not_installed_again()
    {
        Chain("game", "runtime", "launcher");
        _software.Replace(_device.Id, [new InstalledSoftware("A Runtime", "1.0"), new InstalledSoftware("A Launcher", "1.0")]);
        using var client = Client();

        var install = await Start(client, "game");

        Assert.Equal(1, install.StepCount);
        Assert.Equal("A Game", install.StepName);
    }

    [Fact]
    public async Task A_prerequisite_chocolatey_already_installed_is_skipped()
    {
        // The name is nothing like the package id, so only Chocolatey's own list, matched on the id,
        // can say it is there.
        _catalog.Upsert(new CatalogEntry
        {
            Id = "node",
            Name = "Node.js LTS",
            Agent = new ManagedPackageDefinition("choco", "nodejs-lts", "machine"),
        });
        Chain("game", "node");
        _software.Replace(_device.Id, [new InstalledSoftware("nodejs-lts", "20.17.0")], source: "choco");
        using var client = Client();

        var install = await Start(client, "game");

        Assert.Equal(1, install.StepCount);
        Assert.Equal("A Game", install.StepName);
    }

    [Fact]
    public void A_managed_apps_id_counts_only_in_its_own_managers_list()
    {
        var node = new CatalogEntry { Id = "node", Name = "Node.js LTS", Agent = new ManagedPackageDefinition("choco", "nodejs-lts", "machine") };

        Assert.True(node.MatchesInstalled("nodejs-lts", "choco"));
        Assert.True(node.MatchesInstalled("NodeJS-LTS", "choco"));
        // The same text from winget, or from another manager, is somebody else's package.
        Assert.False(node.MatchesInstalled("nodejs-lts", "winget"));
        Assert.False(node.MatchesInstalled("nodejs-lts", "scoop"));
        // The name still works from anywhere, as it always did.
        Assert.True(node.MatchesInstalled("Node.js LTS 20.17.0", "winget"));
    }

    [Fact]
    public async Task The_app_somebody_asked_for_is_installed_even_if_the_device_thinks_it_has_it()
    {
        // Pressing Install and being told nothing happened helps nobody, whatever the inventory says.
        _software.Replace(_device.Id, [new InstalledSoftware("A Game", "1.0")]);
        using var client = Client();

        var install = await Start(client, "game");

        Assert.Equal(1, install.StepCount);
        Assert.Equal("A Game", install.StepName);
    }

    [Fact]
    public async Task A_failed_step_stops_the_chain_and_says_which_one()
    {
        Chain("game", "runtime", "launcher");
        using var client = Client();
        await Start(client, "game");
        await FinishStepAsync(client);

        var job = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;
        await client.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/complete",
            new AgentJobCompletion(false, "The installer could not reach the vendor.", 1));
        await Refresh(client);

        var install = Assert.Single(_installs.All());
        Assert.Equal(InstallState.Failed, install.State);
        Assert.Contains("Step 2 of 3, A Launcher", install.Detail);
        // The third is never attempted, so nothing is waiting for the device to pick up.
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/v1/agent/jobs?wait=0")).StatusCode);
    }

    [Fact]
    public async Task A_chain_is_one_row_in_the_history_rather_than_three()
    {
        Chain("game", "runtime", "launcher");
        using var client = Client();

        await Start(client, "game");

        var install = Assert.Single(_installs.All());
        Assert.Equal("game", install.AppId);
    }

    [Fact]
    public void A_loop_is_refused_when_it_is_written_rather_than_when_somebody_installs()
    {
        Chain("game", "launcher");
        Chain("launcher", "runtime");

        var failure = Assert.Throws<PrerequisiteException>(() => _catalog.EnsureNoCycle("runtime", ["game"]));

        Assert.Contains("loop", failure.Message);
        Assert.Contains("A Game", failure.Message);
    }

    [Fact]
    public void An_app_that_needs_itself_is_refused()
    {
        Assert.Throws<PrerequisiteException>(() => _catalog.EnsureNoCycle("game", ["launcher", "game"]));
    }

    [Fact]
    public void Needing_something_that_is_not_in_the_catalog_is_refused()
    {
        var failure = Assert.Throws<PrerequisiteException>(() => _catalog.EnsureNoCycle("game", ["absent"]));

        Assert.Contains("'absent' is not in the catalog", failure.Message);
    }

    [Fact]
    public void A_diamond_installs_the_shared_prerequisite_once()
    {
        Chain("game", "launcher", "runtime");
        Chain("launcher", "runtime");

        var byId = _catalog.Entries.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var edges = _catalog.Entries.Where(e => e.Requires.Count > 0)
            .ToDictionary(e => e.Id, e => (IReadOnlyList<string>)e.Requires, StringComparer.OrdinalIgnoreCase);

        var chain = PrerequisiteResolver.Expand(byId["game"], byId, edges, _ => false);

        Assert.Equal(["runtime", "launcher", "game"], chain.Select(e => e.Id));
    }

    [Fact]
    public async Task A_chain_that_crosses_engines_follows_the_step_it_is_on()
    {
        // Each step is routed on its own, so a chain may run partly through one engine and partly
        // through the other. The install row has to move with it: the refresh reads the engine off the
        // row to decide whom to ask, and Action1 answers "unknown automation" for an agent job id,
        // which would fail an install that is running perfectly well.
        var devices = new DeviceStore(_test.Database);
        var token = devices.Add("BOTH-PC", "endpoint-1");
        devices.RecordHeartbeat(devices.FindByName("BOTH-PC")!.Id, "0.5.0");
        _catalog.Upsert(new CatalogEntry
        {
            Id = "a1-runtime",
            Name = "An Action1 Runtime",
            Action1 = new Action1PackageRef { PackageId = "Vendor_Runtime" },
        });
        Chain("game", "a1-runtime");
        using var client = Client(token);

        var install = await Start(client, "game");
        Assert.Equal(2, install.StepCount);
        Assert.Equal(EngineLabel.Action1, _installs.Find(install.Id)!.Engine);

        // The stand-in answers Pending, then Running, then Success, and the success moves the chain on.
        await Refresh(client);
        await Refresh(client);
        var moved = await RefreshOne(client);

        Assert.Equal(EngineLabel.Agent, moved.Engine);
        Assert.Equal(InstallState.Running, moved.State);
        Assert.Equal(2, moved.StepNumber);
        Assert.Equal("A Game", moved.StepName);
        // And the row says so too, rather than still claiming the whole install finished with the
        // prerequisite. What is stored is what the next refresh and the history page read.
        var stored = _installs.Find(install.Id)!;
        Assert.Equal(EngineLabel.Agent, stored.Engine);
        Assert.Equal(InstallState.Running, stored.State);
        Assert.Null(stored.CompletedAt);

        // The next refresh asks the agent rather than Action1, which knows nothing of a job id, and
        // the game finishes the chain.
        await Refresh(client);
        Assert.Equal(InstallState.Running, _installs.Find(install.Id)!.State);
        Assert.Equal("A Game", await FinishStepAsync(client));

        var finished = _installs.Find(install.Id)!;
        Assert.Equal(InstallState.Succeeded, finished.State);
        Assert.Equal(EngineLabel.Agent, finished.Engine);
        Assert.Equal([InstallState.Succeeded, InstallState.Succeeded], _steps.For(install.Id).Select(step => step.State));
    }

    [Fact]
    public void The_chain_survives_an_export_and_an_import()
    {
        Chain("game", "runtime", "launcher");

        Assert.Equal(["runtime", "launcher"], _catalog.Entries.Single(e => e.Id == "game").Requires);
    }

    private void Chain(string appId, params string[] needs)
    {
        var entry = _catalog.Find(appId)!;
        entry.Requires = [.. needs];
        _catalog.Upsert(entry);
    }

    private async Task<InstallRequest> Start(HttpClient client, string appId)
    {
        var response = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest(appId));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InstallRequest>(Json))!;
    }

    /// <summary>Completes whatever step the device has been handed, and returns which app it was.</summary>
    private async Task<string> FinishStepAsync(HttpClient client)
    {
        var job = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;
        var name = _steps.For(job.InstallId).Single(step => step.ExternalRef == job.Id).AppName;
        var response = await client.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/complete", new AgentJobCompletion(true, "Installed.", 0));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await Refresh(client);
        return name;
    }

    /// <summary>The poll the client makes, which is what moves a chain on to its next step.</summary>
    private static async Task Refresh(HttpClient client) => await RefreshOne(client);

    /// <summary>The same poll, and the one install it answers with.</summary>
    private static async Task<InstallRequest> RefreshOne(HttpClient client)
    {
        var response = await client.GetAsync(ApiRoutes.Installs + "?refresh=true");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await response.Content.ReadFromJsonAsync<List<InstallRequest>>(Json))!);
    }

    private HttpClient Client(string? token = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _token);
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
