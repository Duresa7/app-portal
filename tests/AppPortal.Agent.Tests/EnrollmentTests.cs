using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AppPortal.Agent.Enrollment;
using AppPortal.Shared;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

[Collection("Agent settings")]
public sealed class EnrollmentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-enrollment-tests", Guid.NewGuid().ToString("N"));
    private string EnrollmentPath => Path.Combine(_root, "enroll.json");
    private string SettingsPath => Path.Combine(_root, "client.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public EnrollmentTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData(null)]
    [InlineData("endpoint-123")]
    public async Task First_start_posts_the_contract_saves_the_token_and_consumes_the_key(string? endpointId)
    {
        WriteEnrollment(endpointId);
        var calls = 0;
        using var http = Client(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://portal.example/prefix/api/v1/enroll", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("ape_test", body.RootElement.GetProperty("key").GetString());
            Assert.Equal(Environment.MachineName, body.RootElement.GetProperty("deviceName").GetString());
            Assert.Equal("machine-123", body.RootElement.GetProperty("machineId").GetString());
            Assert.Equal(endpointId, body.RootElement.GetProperty("action1EndpointId").GetString());
            Assert.Equal(typeof(EnrollmentService).Assembly.GetName().Version!.ToString(3), body.RootElement.GetProperty("agentVersion").GetString());
            Assert.True(File.Exists(EnrollmentPath));
            Assert.False(File.Exists(SettingsPath));
            return Accepted();
        });
        var service = Service(http);
        await service.EnsureEnrolledAsync(CancellationToken.None);
        await service.EnsureEnrolledAsync(CancellationToken.None);

        Assert.Equal(1, calls);
        var settings = JsonSerializer.Deserialize<PortalSettings>(File.ReadAllText(SettingsPath), Json)!;
        Assert.Equal("https://portal.example/prefix", settings.ServerUrl);
        Assert.Equal("apd_test", settings.DeviceToken);
        Assert.Equal(10, settings.RefreshSeconds);
        Assert.False(File.Exists(EnrollmentPath));
        Assert.DoesNotContain("ape_test", File.ReadAllText(SettingsPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Existing_token_discards_even_a_malformed_enrollment_file_without_changing_settings()
    {
        const string original = """{"serverUrl":"https://old.example","deviceToken":"apd_existing","refreshSeconds":42,"updateRepository":"owner/repo","extra":true}""";
        File.WriteAllText(SettingsPath, original);
        File.WriteAllText(EnrollmentPath, "not json");
        using var http = NoRequests();
        await Service(http).EnsureEnrolledAsync(CancellationToken.None);
        Assert.Equal(original, File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(EnrollmentPath));
    }

    [Fact]
    public async Task No_enrollment_file_is_a_no_op()
    {
        using var http = NoRequests();
        await Service(http).EnsureEnrolledAsync(CancellationToken.None);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task Settings_without_a_token_are_enrolled_and_other_options_survive(string? token)
    {
        WriteEnrollment();
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { deviceToken = token, refreshSeconds = 42, updateRepository = "owner/repo", extra = true }));
        using var http = Client((_, _) => Task.FromResult(Accepted()));
        await Service(http).EnsureEnrolledAsync(CancellationToken.None);
        using var settings = JsonDocument.Parse(File.ReadAllText(SettingsPath));
        Assert.Equal("apd_test", settings.RootElement.GetProperty("deviceToken").GetString());
        Assert.Equal(42, settings.RootElement.GetProperty("refreshSeconds").GetInt32());
        Assert.Equal("owner/repo", settings.RootElement.GetProperty("updateRepository").GetString());
        Assert.True(settings.RootElement.GetProperty("extra").GetBoolean());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.OK)]
    public async Task Rejected_enrollment_retains_the_key_and_does_not_save_a_token(HttpStatusCode status)
    {
        WriteEnrollment();
        var original = File.ReadAllText(EnrollmentPath);
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("ape_test") }));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Service(http).EnsureEnrolledAsync(CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("ape_test", error.ToString());
        Assert.Equal(original, File.ReadAllText(EnrollmentPath));
        Assert.False(File.Exists(SettingsPath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"deviceId\":\"id\",\"deviceToken\":\"\",\"deviceName\":\"PC\",\"engines\":[]}")]
    [InlineData("{\"deviceId\":\"id\",\"deviceToken\":\"apd_test\",\"deviceName\":\"PC\"}")]
    public async Task Incomplete_response_does_not_consume_the_key(string body)
    {
        WriteEnrollment();
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(http).EnsureEnrolledAsync(CancellationToken.None));
        Assert.True(File.Exists(EnrollmentPath));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task Malformed_response_does_not_consume_the_key()
    {
        WriteEnrollment();
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("not json") }));
        await Assert.ThrowsAsync<JsonException>(() => Service(http).EnsureEnrolledAsync(CancellationToken.None));
        Assert.True(File.Exists(EnrollmentPath));
        Assert.False(File.Exists(SettingsPath));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"serverUrl\":\"file:///etc\",\"enrollmentKey\":\"ape_test\"}")]
    [InlineData("{\"serverUrl\":\"https://portal.example\",\"enrollmentKey\":\" \"}")]
    public async Task Invalid_configuration_never_sends_the_key(string body)
    {
        File.WriteAllText(EnrollmentPath, body);
        using var http = NoRequests();
        await Assert.ThrowsAsync<InvalidDataException>(() => Service(http).EnsureEnrolledAsync(CancellationToken.None));
        Assert.Equal(body, File.ReadAllText(EnrollmentPath));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task Malformed_configuration_is_retained_for_diagnosis()
    {
        File.WriteAllText(EnrollmentPath, "not json");
        using var http = NoRequests();
        await Assert.ThrowsAsync<JsonException>(() => Service(http).EnsureEnrolledAsync(CancellationToken.None));
        Assert.Equal("not json", File.ReadAllText(EnrollmentPath));
    }

    [Fact]
    public async Task Failed_request_can_retry_with_the_same_machine_identity()
    {
        WriteEnrollment();
        var identities = new List<string>();
        using var http = Client(async (request, ct) =>
        {
            var body = await request.Content!.ReadFromJsonAsync<EnrollmentRequest>(ct);
            identities.Add(body!.MachineId);
            if (identities.Count == 1)
            {
                throw new HttpRequestException("Offline");
            }

            return Accepted();
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => Service(http).EnsureEnrolledAsync(CancellationToken.None));
        Assert.True(File.Exists(EnrollmentPath));
        await Service(http).EnsureEnrolledAsync(CancellationToken.None);
        Assert.Equal(["machine-123", "machine-123"], identities);
        Assert.False(File.Exists(EnrollmentPath));
    }

    [Fact]
    public async Task Cancellation_preserves_the_enrollment_file()
    {
        WriteEnrollment();
        using var cancellation = new CancellationTokenSource();
        using var http = Client(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Accepted();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(http).EnsureEnrolledAsync(cancellation.Token));
        Assert.True(File.Exists(EnrollmentPath));
        Assert.False(File.Exists(SettingsPath));
    }

    [Fact]
    public async Task Failure_to_save_the_token_keeps_the_key_and_cleans_the_temporary_file()
    {
        WriteEnrollment();
        using var http = Client((_, _) =>
        {
            Directory.CreateDirectory(SettingsPath);
            return Task.FromResult(Accepted());
        });
        var error = await Record.ExceptionAsync(() => Service(http).EnsureEnrolledAsync(CancellationToken.None));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.True(File.Exists(EnrollmentPath));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Worker_enrolls_before_its_first_heartbeat()
    {
        WriteEnrollment();
        var previous = new[] { "APPPORTAL_CONFIG", "APPPORTAL_SERVER_URL", "APPPORTAL_DEVICE_TOKEN" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("APPPORTAL_CONFIG", SettingsPath);
            Environment.SetEnvironmentVariable("APPPORTAL_SERVER_URL", null);
            Environment.SetEnvironmentVariable("APPPORTAL_DEVICE_TOKEN", null);
            var routes = new List<string>();
            using var http = Client((request, _) =>
            {
                routes.Add(request.RequestUri!.AbsolutePath);
                if (routes.Count == 1)
                {
                    return Task.FromResult(Accepted());
                }

                Assert.False(File.Exists(EnrollmentPath));
                Assert.Equal("apd_test", request.Headers.Authorization!.Parameter);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new AgentHeartbeatResponse(DateTimeOffset.UtcNow, 120)),
                });
            });
            using var lifetime = new Lifetime();
            using var worker = new HeartbeatWorker(new HeartbeatClient(http), NullLogger<HeartbeatWorker>.Instance,
                lifetime, Path.Combine(_root, "agent.json"), once: true, Service(http));
            await worker.StartAsync(CancellationToken.None);
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(["/prefix/api/v1/enroll", "/prefix/api/v1/agent/heartbeat"], routes);
            Assert.Equal(0, worker.ExitCode);
        }
        finally
        {
            foreach (var (name, value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private void WriteEnrollment(string? endpointId = null)
        => File.WriteAllText(EnrollmentPath, JsonSerializer.Serialize(new EnrollmentSettings("https://portal.example/prefix/", "ape_test", endpointId), Json));

    private EnrollmentService Service(HttpClient http) => new(http, _root, () => "machine-123");
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        => new(new HeartbeatClientTests.Handler(send));
    private static HttpClient NoRequests() => Client((_, _) => throw new Xunit.Sdk.XunitException("Unexpected enrollment request"));
    private static HttpResponseMessage Accepted() => new(HttpStatusCode.Created)
    {
        Content = JsonContent.Create(new EnrollmentResponse("device-123", "apd_test", "PC", ["agent"])),
    };

    public void Dispose() => Directory.Delete(_root, recursive: true);

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
