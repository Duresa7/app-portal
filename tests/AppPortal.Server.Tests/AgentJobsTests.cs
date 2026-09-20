using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Agent;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AppPortal.Server.Tests;

public sealed class AgentJobsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DeviceStore _devices;
    private readonly string _token;
    private readonly DeviceRecord _device;
    private readonly Clock _clock = new();
    private readonly AgentJobStore _jobs;
    private readonly InstallStore _installs;

    public AgentJobsTests()
    {
        _devices = new DeviceStore(_test.Database);
        _token = _devices.Add("AGENT-PC", "endpoint-1");
        _device = _devices.FindByName("AGENT-PC")!;
        _devices.RecordHeartbeat(_device.Id, "0.5.0");
        _jobs = new AgentJobStore(_test.Database, _clock);
        _installs = new InstallStore(_test.Database);
        using var connection = _test.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO catalog_apps (id, name, created_at, updated_at) VALUES ('agent-app', 'Agent App', '', '');
            INSERT INTO catalog_packages (app_id, engine, definition_json)
            VALUES ('agent-app', 'agent', '{"kind":"direct","url":"https://example.test/app.msi","sizeBytes":3000000000}');
            """;
        command.ExecuteNonQuery();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
    }

    [Fact]
    public async Task Agent_only_install_flows_through_progress_and_completion_without_another_install()
    {
        using var client = Client();
        var created = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("agent-app"));
        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var install = (await created.Content.ReadFromJsonAsync<InstallRequest>(Json))!;
        Assert.Equal(EngineLabel.Agent, _installs.Find(install.Id)!.Engine);
        var heartbeat = await client.PostAsJsonAsync("/api/v1/agent/heartbeat", new AgentHeartbeatRequest("0.5.0", null, "Windows"));
        Assert.Equal(60, (await heartbeat.Content.ReadFromJsonAsync<AgentHeartbeatResponse>())!.HeartbeatSeconds);
        var job = (await client.GetFromJsonAsync<AgentJob>("/api/v1/agent/jobs?wait=0"))!;
        Assert.Equal(install.Id, job.InstallId);
        Assert.Equal(3000000000, Assert.IsType<DirectPackageDefinition>(job.Definition).SizeBytes);
        Assert.Equal(job.Id, _installs.Find(install.Id)!.AutomationId);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/v1/agent/jobs")).StatusCode);
        var progress = await client.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/progress", new AgentJobProgress("downloading", 43, "Downloading 43%"));
        Assert.Equal(HttpStatusCode.NoContent, progress.StatusCode);
        var visible = (await client.GetFromJsonAsync<InstallRequest>($"{ApiRoutes.Installs}/{install.Id}", Json))!;
        Assert.Equal(InstallState.Running, visible.State);
        Assert.Equal(43, visible.PercentComplete);
        Assert.Equal("Downloading 43%", visible.Detail);
        var complete = await client.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/complete", new AgentJobCompletion(true, "Installed.", 0));
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        var saved = Assert.Single(_installs.ForDeviceId(_device.Id));
        Assert.Equal(InstallState.Succeeded, saved.State);
        Assert.Equal(100, saved.PercentComplete);
        Assert.NotNull(saved.CompletedAt);
        Assert.False(_jobs.Progress(_device.Id, job.Id, new AgentJobProgress("installing", 0, "Installing")));
    }

    [Fact]
    public void Expiry_retries_three_times_then_fails_the_same_install()
    {
        var id = CreateJob();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var job = _jobs.Lease(_device.Id)!;
            Assert.Equal(id, job.Id);
            Assert.Equal(attempt, job.Attempt);
            _clock.Advance(TimeSpan.FromMinutes(5));
            _jobs.RequeueExpired();
            Assert.Equal(attempt < 3 ? InstallState.Queued : InstallState.Failed, Assert.Single(_installs.All()).State);
        }

        Assert.Null(_jobs.Lease(_device.Id));
        Assert.Contains("three attempts", Assert.Single(_installs.All()).Detail);
    }

    [Fact]
    public void Progress_renews_the_lease_and_stop_returns_it_for_a_new_attempt()
    {
        var id = CreateJob();
        _jobs.Lease(_device.Id);
        _clock.Advance(TimeSpan.FromMinutes(4));
        Assert.True(_jobs.Progress(_device.Id, id, new AgentJobProgress("downloading", 43, "Downloading 43%"), 1));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(_jobs.Lease(_device.Id));
        Assert.True(_jobs.Progress(_device.Id, id, new AgentJobProgress("queued", 0, "Stopped"), 1));
        var resumed = _jobs.Lease(_device.Id)!;
        Assert.Equal(id, resumed.Id);
        Assert.Equal(2, resumed.Attempt);
        Assert.False(_jobs.Complete(_device.Id, id, new AgentJobCompletion(true, null, 0), 1));
        Assert.True(_jobs.Complete(_device.Id, id, new AgentJobCompletion(false, "no executor", 17), 2));
        Assert.Contains("17", Assert.Single(_installs.All()).Detail);
    }

    [Fact]
    public async Task Concurrent_fetches_lease_once_and_only_to_the_owner()
    {
        CreateJob();
        var leases = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => _jobs.Lease(_device.Id))));
        Assert.Single(leases, job => job is not null);
        var job = leases.First(j => j is not null)!;
        var otherToken = _devices.Add("OTHER", "endpoint-2");
        using var other = Client(otherToken);
        Assert.Equal(HttpStatusCode.NoContent, (await other.GetAsync("/api/v1/agent/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/complete", new AgentJobCompletion(true, null, 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync($"/api/v1/agent/jobs/{job.Id}/progress", new AgentJobProgress("installing", 20, "Installing"))).StatusCode);
    }

    [Fact]
    public async Task Waiting_fetch_allows_writes_and_observes_new_jobs()
    {
        using var client = Client();
        var pending = client.GetAsync("/api/v1/agent/jobs?wait=25");
        await Task.Delay(100);
        var id = await Task.Run(CreateJob).WaitAsync(TimeSpan.FromSeconds(3));
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(id, (await response.Content.ReadFromJsonAsync<AgentJob>())!.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waiting_fetch_honours_request_cancellation_and_host_shutdown(bool stopHost)
    {
        // StopApplication does not only raise ApplicationStopping here. The factory lets the entry
        // point's app.Run() run, so stopping runs the whole shutdown and ends by disposing the root
        // service provider. The waiting fetch has to unwind before that: a request still in the
        // pipeline when the provider goes fails with ObjectDisposedException instead of the
        // cancellation this is about. The gate holds the teardown at its first hosted service until
        // the fetch has answered, so the two are ordered rather than racing.
        var gate = new ShutdownGate();
        using var gated = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<IHostedService>(gate)));
        try
        {
            using var client = Client(factory: gated);
            using var cancellation = new CancellationTokenSource();
            var pending = client.GetAsync("/api/v1/agent/jobs?wait=25", cancellation.Token);
            // A head start, so the fetch is inside its wait rather than still being routed. Nothing
            // depends on the length of it: a token already cancelled ends the wait just as well.
            await Task.Delay(100);
            if (stopHost)
            {
                gated.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            }
            else
            {
                cancellation.Cancel();
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            gate.Open();
        }
    }

    /// <summary>
    /// Holds a host's shutdown at its first step. Registered through <c>ConfigureTestServices</c>, so it
    /// is the last hosted service added and therefore the first one stopped: while it waits, nothing
    /// else has been torn down and the service provider is still there.
    /// </summary>
    private sealed class ShutdownGate : IHostedService
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _open.TrySetResult();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        // Bounded by the shutdown timeout as well, so a test that never opens the gate fails rather
        // than hanging.
        public Task StopAsync(CancellationToken cancellationToken) => _open.Task.WaitAsync(cancellationToken);
    }

    [Theory]
    [InlineData("succeeded", 100)]
    [InlineData("downloading", -1)]
    [InlineData("installing", 101)]
    public async Task Invalid_progress_is_refused(string state, int percent)
    {
        var id = CreateJob();
        _jobs.Lease(_device.Id);
        using var client = Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/v1/agent/jobs/{id}/progress", new AgentJobProgress(state, percent, null))).StatusCode);
    }

    [Fact]
    public async Task A_device_without_the_agent_does_not_create_an_agent_job()
    {
        using var client = Client(_devices.Add("NO-AGENT", "endpoint-2"));
        var response = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("agent-app"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Empty(_installs.All());
    }

    [Fact]
    public async Task An_app_with_both_packages_keeps_using_action1()
    {
        using var connection = _test.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO catalog_packages (app_id, engine, definition_json)
            VALUES ('agent-app', 'action1', '{"packageId":"Google_Google_Chrome_1570243626751_builtin","version":"latest"}');
            """;
        command.ExecuteNonQuery();
        using var client = Client();
        var response = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("agent-app"));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(EngineLabel.Action1, Assert.Single(_installs.All()).Engine);
        Assert.Null(_jobs.Lease(_device.Id));
    }

    [Fact]
    public async Task Duplicate_agent_installs_are_rejected()
    {
        using var client = Client();
        Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("agent-app"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("agent-app"))).StatusCode);
        Assert.Single(_installs.All());
    }

    [Fact]
    public void Expired_reports_and_backwards_transitions_do_not_overwrite_the_install()
    {
        var id = CreateJob();
        _jobs.Lease(_device.Id);
        Assert.True(_jobs.Progress(_device.Id, id, new AgentJobProgress("installing", 30, "Installing")));
        Assert.False(_jobs.Progress(_device.Id, id, new AgentJobProgress("downloading", 10, "Downloading 10%")));
        Assert.Equal(30, Assert.Single(_installs.All()).PercentComplete);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(_jobs.Complete(_device.Id, id, new AgentJobCompletion(true, null, 0)));
        Assert.Equal(InstallState.Queued, Assert.Single(_installs.All()).State);
    }

    [Fact]
    public async Task Job_routes_require_a_device_token()
    {
        using var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/agent/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/agent/jobs/unknown/progress", new AgentJobProgress("installing", 0, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/v1/agent/jobs/unknown/complete", new AgentJobCompletion(true, null, 0))).StatusCode);
    }

    private string CreateJob() => _jobs.Create(new InstallRecord
    {
        Id = Guid.NewGuid().ToString("N"),
        DeviceId = _device.Id,
        DeviceName = _device.Name,
        AppId = "agent-app",
        AppName = "Agent App",
        RequestedAt = _clock.GetUtcNow(),
        State = InstallState.Queued,
    }, new DirectPackageDefinition("https://vendor.example/app.exe", new string('a', 64), "exe", "/S", 3_000_000_000L));

    private HttpClient Client(string? token = null, WebApplicationFactory<Program>? factory = null)
    {
        var client = (factory ?? _factory).CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _token);
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
