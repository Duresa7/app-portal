using AppPortal.Server.Devices;
using AppPortal.Shared;

namespace AppPortal.Server.Agent;

/// <summary>
/// Where the SYSTEM agent reports in. It sits under the device API and so authenticates with the same
/// device token the client uses; the answer carries the interval, so the fleet's heartbeat rate is the
/// server's decision rather than something baked into each installed agent.
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentApi(this WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.Prefix + "/agent");
        group.MapPost("/heartbeat", (AgentHeartbeatRequest request, HttpContext context, DeviceStore devices, IConfiguration configuration) =>
        {
            if (string.IsNullOrWhiteSpace(request.AgentVersion) || string.IsNullOrWhiteSpace(request.OsVersion))
            {
                return Results.BadRequest(new ErrorMessage("Agent and OS versions are required."));
            }

            var device = DeviceAuthenticationMiddleware.Current(context);
            devices.RecordHeartbeat(device.Id, request.AgentVersion);
            var seconds = configuration.GetValue("Agent:HeartbeatSeconds", 900);
            return Results.Ok(new AgentHeartbeatResponse(DateTimeOffset.UtcNow, seconds > 0 ? seconds : 900));
        });
    }
}
