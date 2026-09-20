using System.Net.Http.Headers;
using System.Net.Http.Json;

using AppPortal.Shared;

namespace AppPortal.Agent;

/// <summary>
/// The one call the agent makes. It reports what is installed on this PC and reads back how often the
/// server wants to hear from it, so the interval can be changed centrally without shipping a new agent.
/// An answer missing either field is treated as a failure rather than quietly accepted, because the
/// alternative is an agent that settles on an interval nobody chose.
/// </summary>
public sealed class HeartbeatClient(HttpClient http)
{
    public async Task<AgentHeartbeatResponse> SendAsync(PortalSettings settings, AgentHeartbeatRequest heartbeat, CancellationToken ct)
    {
        if (!settings.IsConfigured)
        {
            throw new InvalidOperationException("The server URL and device token must be configured in client.json.");
        }

        var address = new Uri(new Uri(settings.ServerUrl.TrimEnd('/') + "/"), "api/v1/agent/heartbeat");
        using var request = new HttpRequestMessage(HttpMethod.Post, address);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeviceToken);
        request.Content = JsonContent.Create(heartbeat);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadFromJsonAsync<AgentHeartbeatResponse>(ct)
                     ?? throw new InvalidDataException("The heartbeat response was empty.");
        if (answer.ServerTime == default || answer.HeartbeatSeconds <= 0)
        {
            throw new InvalidDataException("The heartbeat response did not contain a time and a positive interval.");
        }

        return answer;
    }
}
