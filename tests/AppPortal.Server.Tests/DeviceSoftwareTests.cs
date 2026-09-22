using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class DeviceSoftwareTests : IDisposable
{
    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DeviceStore _devices;
    private readonly DeviceSoftwareStore _software;
    private readonly string _agentToken;
    private readonly string _bothToken;

    public DeviceSoftwareTests()
    {
        _devices = new DeviceStore(_test.Database);
        _software = new DeviceSoftwareStore(_test.Database);
        // A device whose only engine is the agent, so it has no Action1 endpoint to ask.
        _agentToken = _devices.Add("AGENT-ONLY", "");
        _bothToken = _devices.Add("BOTH", "endpoint-1234");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
    }

    [Fact]
    public async Task What_the_agent_reports_is_what_the_device_sees_under_installed()
    {
        using var client = Client(_agentToken);

        var posted = await client.PostAsJsonAsync("/api/v1/agent/software", new[]
        {
            new InstalledSoftware("Steam", "2.10.91.91"),
            new InstalledSoftware("7-Zip", "23.01"),
        });
        Assert.Equal(HttpStatusCode.NoContent, posted.StatusCode);

        var installed = (await client.GetFromJsonAsync<List<InstalledApp>>(ApiRoutes.Installed))!;

        Assert.Equal(["7-Zip", "Steam"], installed.Select(a => a.Name));
        Assert.Equal("2.10.91.91", installed.Single(a => a.Name == "Steam").Version);
    }

    [Fact]
    public async Task A_device_with_no_endpoint_is_not_asked_for_an_inventory_it_cannot_have()
    {
        // Before the agent has said anything this device simply has nothing installed. Reaching for an
        // Action1 endpoint it does not have would fail the whole call instead.
        using var client = Client(_agentToken);

        var installed = await client.GetAsync(ApiRoutes.Installed);

        Assert.Equal(HttpStatusCode.OK, installed.StatusCode);
        Assert.Empty((await installed.Content.ReadFromJsonAsync<List<InstalledApp>>())!);
    }

    [Fact]
    public async Task A_report_replaces_the_last_one_rather_than_adding_to_it()
    {
        using var client = Client(_agentToken);

        await client.PostAsJsonAsync("/api/v1/agent/software", new[] { new InstalledSoftware("Gone", "1.0") });
        await client.PostAsJsonAsync("/api/v1/agent/software", new[] { new InstalledSoftware("Kept", "2.0") });

        // Software somebody removed has to leave the record, which a merge would never let it do.
        var installed = (await client.GetFromJsonAsync<List<InstalledApp>>(ApiRoutes.Installed))!;
        Assert.Equal("Kept", Assert.Single(installed).Name);
    }

    [Fact]
    public async Task Both_inventories_appear_and_the_one_that_knows_the_vendor_wins()
    {
        using var client = Client(_bothToken);
        var fromAction1 = (await client.GetFromJsonAsync<List<InstalledApp>>(ApiRoutes.Installed))!;
        Assert.NotEmpty(fromAction1);
        var shared = fromAction1[0];

        await client.PostAsJsonAsync("/api/v1/agent/software", new[]
        {
            new InstalledSoftware(shared.Name, "99.0"),
            new InstalledSoftware("Agent Only App", "1.2.3"),
        });

        var merged = (await client.GetFromJsonAsync<List<InstalledApp>>(ApiRoutes.Installed))!;

        Assert.Contains(merged, a => a.Name == "Agent Only App" && a.Version == "1.2.3");
        // Action1 reports a vendor and the agent cannot, so its row stays whole.
        var kept = merged.Single(a => a.Name == shared.Name);
        Assert.Equal(shared.Vendor, kept.Vendor);
        Assert.Equal(shared.Version, kept.Version);
        Assert.Equal(merged.Select(a => a.Name).Distinct().Count(), merged.Count);
    }

    [Fact]
    public async Task An_unreasonable_report_is_refused()
    {
        using var client = Client(_agentToken);
        var many = Enumerable.Range(0, 5001).Select(i => new InstalledSoftware($"App {i}", "1.0")).ToArray();

        var posted = await client.PostAsJsonAsync("/api/v1/agent/software", many);

        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
        Assert.Empty(_software.ForDevice(_devices.FindByName("AGENT-ONLY")!.Id));
    }

    [Fact]
    public async Task One_device_never_sees_another_device_software()
    {
        using var agent = Client(_agentToken);
        await agent.PostAsJsonAsync("/api/v1/agent/software", new[] { new InstalledSoftware("Private App", "1.0") });

        using var other = Client(_bothToken);
        var installed = (await other.GetFromJsonAsync<List<InstalledApp>>(ApiRoutes.Installed))!;

        Assert.DoesNotContain(installed, a => a.Name == "Private App");
    }

    [Fact]
    public void Blank_and_repeated_names_are_dropped_before_they_are_stored()
    {
        var id = _devices.FindByName("AGENT-ONLY")!.Id;

        _software.Replace(id, [
            new InstalledSoftware("  Steam  ", " 1.0 "),
            new InstalledSoftware("steam", "2.0"),
            new InstalledSoftware("   ", "3.0"),
        ]);

        var stored = Assert.Single(_software.ForDevice(id));
        Assert.Equal("Steam", stored.Name);
        Assert.Equal("1.0", stored.Version);
    }

    [Fact]
    public async Task Each_source_replaces_only_its_own_rows_and_no_source_means_winget()
    {
        using var client = Client(_agentToken);
        var id = _devices.FindByName("AGENT-ONLY")!.Id;

        await client.PostAsJsonAsync("/api/v1/agent/software", new[] { new InstalledSoftware("Steam", "2.10") });
        await client.PostAsJsonAsync("/api/v1/agent/software?source=scoop", new[] { new InstalledSoftware("7zip", "24.08") });
        // An agent from before package managers sends no source. It must go on replacing exactly the
        // rows it always did, and leave Scoop's alone.
        await client.PostAsJsonAsync("/api/v1/agent/software", new[] { new InstalledSoftware("Discord", "1.0") });
        Assert.Equal(["7zip:scoop", "Discord:winget"], _software.ForDevice(id).Select(row => row.Name + ":" + row.Source));

        // And Scoop's own empty list clears only Scoop's rows.
        await client.PostAsJsonAsync("/api/v1/agent/software?source=scoop", Array.Empty<InstalledSoftware>());
        Assert.Equal("Discord", Assert.Single(_software.ForDevice(id)).Name);
    }

    [Fact]
    public async Task A_source_this_server_has_never_heard_of_is_refused()
    {
        using var client = Client(_agentToken);

        var posted = await client.PostAsJsonAsync("/api/v1/agent/software?source=apt", new[] { new InstalledSoftware("vim", "9.0") });

        Assert.Equal(HttpStatusCode.BadRequest, posted.StatusCode);
        Assert.Empty(_software.ForDevice(_devices.FindByName("AGENT-ONLY")!.Id));
    }

    [Fact]
    public async Task A_package_a_manager_installed_is_matched_to_its_app_by_id_and_says_where_it_came_from()
    {
        // Scoop lists the app without its bucket, and the app's name is nothing like its id, so only the
        // id can tie the two together. That is what brings back the Remove button.
        new CatalogStore(_test.Database, "").Upsert(new CatalogEntry
        {
            Id = "vscode",
            Name = "Visual Studio Code",
            Agent = new ManagedPackageDefinition("scoop", "extras/vscode", "user"),
        });
        using var client = Client(_agentToken);
        await client.PostAsJsonAsync("/api/v1/agent/software?source=scoop", new[] { new InstalledSoftware("vscode", "1.93.1") });
        await client.PostAsJsonAsync("/api/v1/agent/software", new[] { new InstalledSoftware("vscode", "0.1") });

        var installed = (await client.GetFromJsonAsync<List<InstalledApp>>(ApiRoutes.Installed))!;

        // Both rows are there, and only the one Scoop reported is the catalog's app.
        Assert.Equal(2, installed.Count);
        var scoop = Assert.Single(installed, a => a.Source == "scoop");
        Assert.Equal("vscode", scoop.CatalogAppId);
        Assert.Null(Assert.Single(installed, a => a.Source == "winget").CatalogAppId);
    }

    private HttpClient Client(string token)
    {
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
