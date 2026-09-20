using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AppPortal.Shared;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

[CollectionDefinition("Agent settings", DisableParallelization = true)]
public sealed class AgentSettingsCollection;

[Collection("Agent settings")]
public sealed class HeartbeatWorkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _previous = new();

    public HeartbeatWorkerTests()
    {
        Directory.CreateDirectory(_root);
        SetEnvironment("APPPORTAL_CONFIG", Path.Combine(_root, "client.json"));
        SetEnvironment("APPPORTAL_SERVER_URL", "https://portal.example");
        SetEnvironment("APPPORTAL_DEVICE_TOKEN", "apd_test");
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, 0, "Succeeded")]
    [InlineData(HttpStatusCode.Unauthorized, 1, "Failed")]
    [InlineData(HttpStatusCode.ServiceUnavailable, 1, "Failed")]
    public async Task Once_writes_the_outcome_and_stops_with_the_right_exit_code(HttpStatusCode status, int exitCode, string outcome)
    {
        var calls = 0;
        using var http = new HttpClient(new HeartbeatClientTests.Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = JsonContent.Create(new AgentHeartbeatResponse(DateTimeOffset.UtcNow, 120)),
            });
        }));
        using var lifetime = new Lifetime();
        var path = Path.Combine(_root, "agent.json");
        using var worker = new HeartbeatWorker(new HeartbeatClient(http), NullLogger<HeartbeatWorker>.Instance, lifetime, path, once: true);
        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, calls);
        Assert.Equal(exitCode, worker.ExitCode);
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        var saved = JsonSerializer.Deserialize<AgentStatus>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(outcome, saved!.Outcome);
        Assert.InRange(saved.LastHeartbeatAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.DoesNotContain("apd_test", File.ReadAllText(path));
    }

    [Fact]
    public async Task Service_retries_after_a_failed_heartbeat_and_stops_on_cancellation()
    {
        var calls = 0;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new HeartbeatClientTests.Handler((_, _) =>
        {
            calls++;
            if (calls == 2)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (calls == 3)
            {
                retried.SetResult();
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AgentHeartbeatResponse(DateTimeOffset.UtcNow, 1)),
            });
        }));
        using var lifetime = new Lifetime();
        using var worker = new HeartbeatWorker(new HeartbeatClient(http), NullLogger<HeartbeatWorker>.Instance, lifetime, Path.Combine(_root, "agent.json"), once: false);
        await worker.StartAsync(CancellationToken.None);
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(deadline.Token);
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
        Assert.Equal(3, calls);
        Assert.False(lifetime.ApplicationStopping.IsCancellationRequested);
    }

    [Fact]
    public void Shared_settings_preserve_the_file_shape_and_environment_overrides()
    {
        File.WriteAllText(Path.Combine(_root, "client.json"), """
            {"serverUrl":"https://file.example","deviceToken":"apd_file","refreshSeconds":25,"updateRepository":"owner/repo"}
            """);
        var settings = PortalSettings.Load();
        Assert.Equal("https://portal.example", settings.ServerUrl);
        Assert.Equal("apd_test", settings.DeviceToken);
        Assert.Equal(25, settings.RefreshSeconds);
        Assert.Equal("owner/repo", settings.UpdateRepository);
        SetEnvironment("APPPORTAL_SERVER_URL", null);
        SetEnvironment("APPPORTAL_DEVICE_TOKEN", null);
        settings = PortalSettings.Load();
        Assert.Equal("https://file.example", settings.ServerUrl);
        Assert.Equal("apd_file", settings.DeviceToken);
    }

    [Fact]
    public void Jitter_spreads_the_server_interval_within_ten_percent()
    {
        var delays = Enumerable.Range(0, 100).Select(_ => HeartbeatWorker.NextDelay(900).TotalSeconds).ToArray();
        Assert.All(delays, delay => Assert.InRange(delay, 810, 990));
        Assert.True(delays.Distinct().Count() > 1);
    }

    private void SetEnvironment(string name, string? value)
    {
        _previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        Directory.Delete(_root, recursive: true);
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}
