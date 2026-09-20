using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Agent;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

/// <summary>
/// Installed is not the same as finished. Software whose driver loads at boot is registered by its
/// installer and does nothing until the PC restarts, so an exit code of zero reports a success the
/// person discovers is not one. These are the states that keep the install honest until it is.
/// </summary>
public sealed class RestartStateTests : IDisposable
{
    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InstallStore _installs;
    private readonly DeviceSoftwareStore _software;
    private readonly DeviceRecord _device;
    private readonly string _token;

    public RestartStateTests()
    {
        var devices = new DeviceStore(_test.Database);
        _installs = new InstallStore(_test.Database);
        _software = new DeviceSoftwareStore(_test.Database);
        _token = devices.Add("AGENT-PC", "");
        _device = devices.FindByName("AGENT-PC")!;
        devices.RecordHeartbeat(_device.Id, "0.5.0");
        new CatalogStore(_test.Database, "").Upsert(new CatalogEntry
        {
            Id = "driver",
            Name = "Driver App",
            Agent = new WingetPackageDefinition("Vendor.Driver", "machine"),
        });

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
    }

    [Fact]
    public async Task An_install_that_needs_a_restart_is_not_reported_as_finished()
    {
        using var client = Client();
        var job = await Start(client);

        await Complete(client, job, needsRestart: true);

        var install = Assert.Single(_installs.All());
        Assert.Equal(InstallState.Running, install.State);
        Assert.Equal(RebootState.Pending, install.RebootState);
        Assert.Equal(RebootState.WaitingDetail, install.Detail);
    }

    [Fact]
    public async Task An_ordinary_install_is_untouched_by_any_of_this()
    {
        using var client = Client();
        var job = await Start(client);

        await Complete(client, job, needsRestart: false);

        var install = Assert.Single(_installs.All());
        Assert.Equal(InstallState.Succeeded, install.State);
        Assert.Null(install.RebootState);
    }

    [Fact]
    public async Task A_restart_finishes_it_when_the_software_is_still_there()
    {
        using var client = Client();
        await Complete(client, await Start(client), needsRestart: true);
        _software.Replace(_device.Id, [new InstalledSoftware("Driver App", "1.0")]);

        await Heartbeat(client, bootedAgo: TimeSpan.Zero);

        var install = Assert.Single(_installs.All());
        Assert.Equal(InstallState.Succeeded, install.State);
        Assert.Equal(RebootState.Confirmed, install.RebootState);
        Assert.Contains("restarted", install.Detail);
    }

    [Fact]
    public async Task A_restart_that_leaves_nothing_behind_fails_it_rather_than_claiming_success()
    {
        using var client = Client();
        await Complete(client, await Start(client), needsRestart: true);
        _software.Replace(_device.Id, [new InstalledSoftware("Something Else", "1.0")]);

        await Heartbeat(client, bootedAgo: TimeSpan.Zero);

        var install = Assert.Single(_installs.All());
        Assert.Equal(InstallState.Failed, install.State);
        Assert.Equal("The software was not there after the restart.", install.Detail);
    }

    [Fact]
    public async Task A_restart_finishes_a_per_user_install_that_only_that_person_can_see()
    {
        // Software installed into somebody's profile is reported under their account, not against the
        // device. Looking only at the machine-wide list finds nothing and calls a perfectly good
        // install a failure, which is exactly what the person was told would not happen.
        new CatalogStore(_test.Database, "").Upsert(new CatalogEntry
        {
            Id = "profile-app",
            Name = "Profile App",
            Agent = new WingetPackageDefinition("Vendor.Profile", "user"),
        });
        using var client = Client(Requester);
        await Complete(client, await Start(client, "profile-app"), needsRestart: true);
        _software.Replace(_device.Id, [new InstalledSoftware("Something Machine Wide", "1.0")]);
        _software.Replace(_device.Id, [new InstalledSoftware("Profile App", "1.0")], Requester);

        await Heartbeat(client, bootedAgo: TimeSpan.Zero);

        var install = Assert.Single(_installs.All());
        Assert.Equal(InstallState.Succeeded, install.State);
        Assert.Equal(RebootState.Confirmed, install.RebootState);
    }

    [Fact]
    public async Task A_per_user_install_whose_software_is_gone_after_the_restart_still_fails()
    {
        new CatalogStore(_test.Database, "").Upsert(new CatalogEntry
        {
            Id = "profile-app",
            Name = "Profile App",
            Agent = new WingetPackageDefinition("Vendor.Profile", "user"),
        });
        using var client = Client(Requester);
        await Complete(client, await Start(client, "profile-app"), needsRestart: true);
        _software.Replace(_device.Id, [new InstalledSoftware("Something Else", "1.0")], Requester);

        await Heartbeat(client, bootedAgo: TimeSpan.Zero);

        var install = Assert.Single(_installs.All());
        Assert.Equal(InstallState.Failed, install.State);
        Assert.Equal("The software was not there after the restart.", install.Detail);
    }

    [Fact]
    public async Task A_device_that_cannot_read_its_own_software_is_given_the_benefit_of_the_doubt()
    {
        // Reporting nothing is not the same as reporting the software is gone, and a history rewritten
        // on the strength of a failed inventory sweep is worse than one that is merely optimistic.
        using var client = Client();
        await Complete(client, await Start(client), needsRestart: true);

        await Heartbeat(client, bootedAgo: TimeSpan.Zero);

        Assert.Equal(InstallState.Succeeded, Assert.Single(_installs.All()).State);
    }

    [Fact]
    public async Task A_device_that_has_not_restarted_yet_keeps_waiting()
    {
        using var client = Client();
        await Complete(client, await Start(client), needsRestart: true);

        // Booted long before the install, so this heartbeat is not evidence of anything.
        await Heartbeat(client, bootedAgo: TimeSpan.FromDays(3));

        var install = Assert.Single(_installs.All());
        Assert.Equal(InstallState.Running, install.State);
        Assert.Equal(RebootState.Pending, install.RebootState);
    }

    [Fact]
    public async Task The_administrator_can_find_what_is_waiting_for_a_restart()
    {
        using var client = Client();
        await Complete(client, await Start(client), needsRestart: true);

        var waiting = _installs.List(new InstallFilter(AwaitingRestart: true), ListQuery.All);
        var everything = _installs.List(InstallFilter.None, ListQuery.All);

        Assert.Single(waiting.Rows);
        Assert.Single(everything.Rows);
    }

    private static async Task<AgentJob> Start(HttpClient client, string appId = "driver")
    {
        var created = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest(appId));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        return (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;
    }

    private static async Task Complete(HttpClient client, AgentJob job, bool needsRestart)
    {
        var completion = new AgentJobCompletion(true, needsRestart ? "Installed. This PC has to restart to finish." : "Installed.", 0, needsRestart);
        var response = await client.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/complete", completion);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task Heartbeat(HttpClient client, TimeSpan bootedAgo)
    {
        var body = new AgentHeartbeatRequest("0.5.0", null, "Windows", DateTimeOffset.UtcNow - bootedAgo);
        var response = await client.PostAsJsonAsync("/api/v1/agent/heartbeat", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The account a per-user install is made for, as the client sends it.</summary>
    private const string Requester = @"CONTOSO\ada";

    private HttpClient Client(string? requester = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (requester is not null)
        {
            client.DefaultRequestHeaders.Add(ApiHeaders.Requester, requester);
        }

        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
