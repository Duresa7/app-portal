using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using AppPortal.Server.Agent;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

/// <summary>
/// A per-user install cannot run until the person who asked for it is at the PC. What that costs is
/// covered here: the job has to leave the queue without failing, come back when they sign in, and not
/// hold up the installs behind it while it waits.
/// </summary>
public sealed class WaitingForUserTests : IDisposable
{
    private const string Ada = @"CONTOSO\ada";
    private const string Bob = @"CONTOSO\bob";

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DeviceStore _devices;
    private readonly InstallStore _installs;
    private readonly DeviceRecord _device;
    private readonly string _token;

    public WaitingForUserTests()
    {
        _devices = new DeviceStore(_test.Database);
        _installs = new InstallStore(_test.Database);
        _token = _devices.Add("AGENT-PC", "");
        _device = _devices.FindByName("AGENT-PC")!;
        _devices.RecordHeartbeat(_device.Id, "0.5.0");
        using (var connection = _test.Database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO catalog_apps (id, name, created_at, updated_at) VALUES ('chat', 'Chat App', '', '');
                INSERT INTO catalog_packages (app_id, engine, definition_json)
                VALUES ('chat', 'agent', '{"kind":"winget","id":"Vendor.Chat","scope":"user"}');
                INSERT INTO catalog_apps (id, name, created_at, updated_at) VALUES ('zip', 'Zipper', '', '');
                INSERT INTO catalog_packages (app_id, engine, definition_json)
                VALUES ('zip', 'agent', '{"kind":"winget","id":"Vendor.Zip","scope":"machine"}');
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
    public async Task The_job_carries_the_account_that_asked_so_the_agent_knows_whose_session_to_use()
    {
        using var client = Client(Ada);
        await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chat"));

        var job = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;

        Assert.Equal(Ada, job.Requester);
    }

    [Fact]
    public async Task A_parked_job_leaves_the_queue_without_failing_and_comes_back_when_she_signs_in()
    {
        using var client = Client(Ada);
        await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chat"));
        var job = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;

        var parked = await client.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/progress",
            new AgentJobProgress("waiting_for_user", 0, $"Waiting for {Ada} to sign in."));
        Assert.Equal(HttpStatusCode.NoContent, parked.StatusCode);

        // Still running as far as anybody watching is concerned, and saying why.
        var install = _installs.All().Single(i => i.AppId == "chat");
        Assert.Equal(InstallState.Running, install.State);
        Assert.Contains("Waiting for", install.Detail);

        // Nobody signed in: nothing to hand out.
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/v1/agent/jobs?wait=0")).StatusCode);

        // She signs in, and the same job comes back rather than a second one being made.
        var resumed = await Jobs(client, Ada);
        Assert.Equal(job.Id, resumed!.Id);
        Assert.Single(_installs.All(), i => i.AppId == "chat");
    }

    [Fact]
    public async Task Somebody_else_signing_in_does_not_start_her_install()
    {
        using var client = Client(Ada);
        await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chat"));
        var job = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;
        await client.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/progress", new AgentJobProgress("waiting_for_user", 0, "Waiting."));

        // It would land in his profile, which is not what she asked for.
        Assert.Null(await Jobs(client, Bob));
    }

    [Fact]
    public async Task A_parked_job_does_not_hold_up_the_installs_behind_it()
    {
        using var client = Client(Ada);
        await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chat"));
        var parked = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;
        await client.PostAsJsonAsync($"/api/v1/agent/jobs/{parked.Id}/progress", new AgentJobProgress("waiting_for_user", 0, "Waiting."));

        // A machine-wide install asked for afterwards must not wait behind somebody's absence.
        await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("zip"));
        var next = await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0");

        Assert.NotNull(next);
        Assert.NotEqual(parked.Id, next.Id);
    }

    [Fact]
    public async Task A_job_nobody_came_back_for_fails_after_a_week_and_says_who_it_waited_for()
    {
        using var client = Client(Ada);
        await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chat"));
        var job = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;
        await client.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/progress", new AgentJobProgress("waiting_for_user", 0, "Waiting."));

        Age(job.Id, days: 8);
        await client.GetAsync("/api/v1/agent/jobs?wait=0");

        var install = _installs.All().Single(i => i.AppId == "chat");
        Assert.Equal(InstallState.Failed, install.State);
        Assert.Equal($"Nobody signed in as {Ada} within 7 days.", install.Detail);
    }

    [Fact]
    public async Task A_job_still_inside_the_week_is_left_alone()
    {
        using var client = Client(Ada);
        await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chat"));
        var job = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;
        await client.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/progress", new AgentJobProgress("waiting_for_user", 0, "Waiting."));

        Age(job.Id, days: 6);
        await client.GetAsync("/api/v1/agent/jobs?wait=0");

        Assert.Equal(InstallState.Running, _installs.All().Single(i => i.AppId == "chat").State);
    }

    [Fact]
    public async Task Her_own_software_is_shown_to_her_and_not_to_him()
    {
        using var ada = Client(Ada);
        await ada.PostAsJsonAsync("/api/v1/agent/software?account=" + Uri.EscapeDataString(Ada),
            new[] { new InstalledSoftware("Chat App", "1.0") });
        await ada.PostAsJsonAsync("/api/v1/agent/software", new[] { new InstalledSoftware("Zipper", "2.0") });

        var hers = (await ada.GetFromJsonAsync<List<InstalledApp>>(ApiRoutes.Installed))!;
        using var bob = Client(Bob);
        var his = (await bob.GetFromJsonAsync<List<InstalledApp>>(ApiRoutes.Installed))!;

        // The machine-wide sweep is everyone's; what she installed into her own profile is hers.
        Assert.Equal(["Chat App", "Zipper"], hers.Select(a => a.Name));
        Assert.Equal(["Zipper"], his.Select(a => a.Name));
    }

    [Fact]
    public async Task Sweeping_one_profile_does_not_erase_the_device_list()
    {
        using var client = Client(Ada);
        await client.PostAsJsonAsync("/api/v1/agent/software", new[] { new InstalledSoftware("Zipper", "2.0") });

        await client.PostAsJsonAsync("/api/v1/agent/software?account=" + Uri.EscapeDataString(Ada),
            new[] { new InstalledSoftware("Chat App", "1.0") });

        Assert.Contains((await client.GetFromJsonAsync<List<InstalledApp>>(ApiRoutes.Installed))!, a => a.Name == "Zipper");
    }

    private async Task<AgentJob?> Jobs(HttpClient client, string signedIn)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/jobs?wait=0");
        request.Headers.TryAddWithoutValidation(ApiHeaders.SignedInAccounts, signedIn);
        using var response = await client.SendAsync(request);
        return response.StatusCode == HttpStatusCode.NoContent
            ? null
            : await response.Content.ReadFromJsonAsync<AgentJob>();
    }

    /// <summary>Moves a parked job back in time, so the sweep sees it as abandoned.</summary>
    private void Age(string jobId, int days)
    {
        using var connection = _test.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE agent_jobs SET updated_at = @when WHERE id = @id;";
        command.Parameters.AddWithValue("@when", DateTimeOffset.UtcNow.AddDays(-days).ToString("O"));
        command.Parameters.AddWithValue("@id", jobId);
        command.ExecuteNonQuery();
    }

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
