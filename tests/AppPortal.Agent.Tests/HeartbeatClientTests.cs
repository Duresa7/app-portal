using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using AppPortal.Shared;

namespace AppPortal.Agent.Tests;

public sealed class HeartbeatClientTests
{
    private static readonly PortalSettings Settings = new() { ServerUrl = "https://portal.example/prefix", DeviceToken = "apd_test" };
    private static readonly AgentHeartbeatRequest Heartbeat = new("0.4.0", "0.3.0", "Windows 11");

    [Fact]
    public async Task Sends_the_device_token_and_versions_and_reads_the_server_interval()
    {
        var expected = new AgentHeartbeatResponse(DateTimeOffset.UtcNow, 120);
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://portal.example/prefix/api/v1/agent/heartbeat", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("apd_test", request.Headers.Authorization.Parameter);
            var body = await request.Content!.ReadAsStringAsync(ct);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("0.4.0", json.RootElement.GetProperty("agentVersion").GetString());
            Assert.Equal("0.3.0", json.RootElement.GetProperty("clientVersion").GetString());
            Assert.Equal("Windows 11", json.RootElement.GetProperty("osVersion").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(expected) };
        }));

        Assert.Equal(expected, await new HeartbeatClient(http).SendAsync(Settings, Heartbeat, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Unsuccessful_http_status_is_a_failure(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(status))));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new HeartbeatClient(http).SendAsync(Settings, Heartbeat, CancellationToken.None));
        Assert.Equal(status, error.StatusCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"serverTime\":\"2026-09-20T00:00:00Z\",\"heartbeatSeconds\":0}")]
    [InlineData("{\"serverTime\":\"2026-09-20T00:00:00Z\",\"heartbeatSeconds\":-1}")]
    public async Task Incomplete_responses_do_not_become_successful_heartbeats(string body)
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
        await Assert.ThrowsAsync<InvalidDataException>(() => new HeartbeatClient(http).SendAsync(Settings, Heartbeat, CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_json_is_a_failure()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") })));
        await Assert.ThrowsAsync<JsonException>(() => new HeartbeatClient(http).SendAsync(Settings, Heartbeat, CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_reaches_the_pending_request()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HeartbeatClient(http).SendAsync(Settings, Heartbeat, cancellation.Token));
    }

    [Fact]
    public async Task Missing_configuration_does_not_send_a_request()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected request")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HeartbeatClient(http).SendAsync(new PortalSettings(), Heartbeat, CancellationToken.None));
    }

    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
