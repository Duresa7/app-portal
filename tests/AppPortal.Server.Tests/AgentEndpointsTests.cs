using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using AppPortal.Server.Devices;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class AgentEndpointsTests : IDisposable
{
    private const string Route = "/api/v1/agent/heartbeat";
    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DeviceStore _devices;
    private readonly string _token;

    public AgentEndpointsTests()
    {
        _devices = new DeviceStore(_test.Database);
        _token = _devices.Add("AGENT-PC", "endpoint-1");
        _devices.Add("OTHER-PC", "endpoint-2");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
    }

    [Fact]
    public async Task Heartbeats_update_only_the_authenticated_device_and_preserve_its_settings()
    {
        var original = _devices.FindByName("AGENT-PC")!;
        original.EnginePreference = "action1";
        _devices.Update(original);
        using var client = Client(_token);
        var before = DateTimeOffset.UtcNow;
        var response = await client.PostAsJsonAsync(Route, new AgentHeartbeatRequest("0.4.0", "0.3.0", "Windows 11"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var answer = await response.Content.ReadFromJsonAsync<AgentHeartbeatResponse>();
        Assert.Equal(900, answer!.HeartbeatSeconds);
        Assert.InRange(answer.ServerTime, before, DateTimeOffset.UtcNow);
        var device = _devices.Find(original.Id)!;
        Assert.True(device.HasAgent);
        Assert.Equal("0.4.0", device.AgentVersion);
        Assert.InRange(device.LastSeenAt!.Value, before, DateTimeOffset.UtcNow);
        Assert.Equal(original.EndpointId, device.EndpointId);
        Assert.Equal(original.EnginePreference, device.EnginePreference);
        Assert.Equal(original.TokenSha256, device.TokenSha256);
        var other = _devices.FindByName("OTHER-PC")!;
        Assert.False(other.HasAgent);
        Assert.Null(other.LastSeenAt);
        Assert.Null(other.AgentVersion);

        before = DateTimeOffset.UtcNow;
        (await client.PostAsJsonAsync(Route, new AgentHeartbeatRequest("0.4.1", null, "Windows 11"))).EnsureSuccessStatusCode();
        device = _devices.Find(original.Id)!;
        Assert.True(device.HasAgent);
        Assert.Equal("0.4.1", device.AgentVersion);
        Assert.InRange(device.LastSeenAt!.Value, before, DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("apd_wrong")]
    [InlineData("disabled")]
    public async Task Missing_invalid_or_disabled_credentials_cannot_record_an_agent(string? token)
    {
        if (token == "disabled")
        {
            var device = _devices.FindByName("AGENT-PC")!;
            device.Enabled = false;
            _devices.Update(device);
            token = _token;
        }

        using var client = Client(token);
        var response = await client.PostAsJsonAsync(Route, new AgentHeartbeatRequest("0.4.0", null, "Windows 11"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(_devices.FindByName("AGENT-PC")!.HasAgent);
        Assert.Null(_devices.FindByName("AGENT-PC")!.LastSeenAt);
    }

    [Theory]
    [InlineData("", "Windows 11")]
    [InlineData("0.4.0", "")]
    public async Task Missing_versions_are_rejected(string agentVersion, string osVersion)
    {
        using var client = Client(_token);
        var response = await client.PostAsJsonAsync(Route, new AgentHeartbeatRequest(agentVersion, null, osVersion));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(_devices.FindByName("AGENT-PC")!.HasAgent);
    }

    [Fact]
    public async Task Server_can_tune_the_interval()
    {
        using var factory = _factory.WithWebHostBuilder(builder => builder.UseSetting("Agent:HeartbeatSeconds", "120"));
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        var response = await client.PostAsJsonAsync(Route, new AgentHeartbeatRequest("0.4.0", null, "Windows 11"));
        var answer = await response.Content.ReadFromJsonAsync<AgentHeartbeatResponse>();
        Assert.Equal(120, answer!.HeartbeatSeconds);
    }

    private HttpClient Client(string? token)
    {
        var client = _factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
