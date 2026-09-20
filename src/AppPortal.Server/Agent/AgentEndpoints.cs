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
    /// <summary>A bound on one report, so a device cannot fill the database by mistake or on purpose.</summary>
    private const int MaxSoftwareEntries = 5000;

    public static void MapAgentApi(this WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.Prefix + "/agent");
        group.MapPost("/heartbeat", (AgentHeartbeatRequest request, HttpContext context, DeviceStore devices, IConfiguration configuration, AgentJobStore jobs) =>
        {
            if (string.IsNullOrWhiteSpace(request.AgentVersion) || string.IsNullOrWhiteSpace(request.OsVersion))
            {
                return Results.BadRequest(new ErrorMessage("Agent and OS versions are required."));
            }

            var device = DeviceAuthenticationMiddleware.Current(context);
            devices.RecordHeartbeat(device.Id, request.AgentVersion);
            var seconds = jobs.HasQueued(device.Id) ? 60 : configuration.GetValue("Agent:HeartbeatSeconds", 900);
            return Results.Ok(new AgentHeartbeatResponse(DateTimeOffset.UtcNow, seconds > 0 ? seconds : 900));
        });

        group.MapGet("/jobs", async (int? wait, HttpContext context, AgentJobStore jobs, IHostApplicationLifetime lifetime, CancellationToken ct) =>
        {
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
            var token = stopping.Token;
            var device = DeviceAuthenticationMiddleware.Current(context);
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            var duration = TimeSpan.FromSeconds(Math.Clamp(wait ?? 0, 0, 25));
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var job = jobs.Lease(device.Id);
                if (job is not null)
                {
                    return Results.Ok(job);
                }

                var remaining = duration - deadline.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return Results.NoContent();
                }

                // Lease has closed its connection: a waiting device must not block another writer.
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), token);
            }
        });

        group.MapPost("/software", (IReadOnlyList<InstalledSoftware> request, HttpContext context, DeviceSoftwareStore software) =>
        {
            // The whole list, every time. A device that had software removed has to be able to say so,
            // and a merge would leave anything the agent stopped reporting on the record for ever.
            var device = DeviceAuthenticationMiddleware.Current(context);
            if (request.Count > MaxSoftwareEntries)
            {
                return Results.BadRequest(new ErrorMessage($"A device may report at most {MaxSoftwareEntries} pieces of software."));
            }

            software.Replace(device.Id, request);
            return Results.NoContent();
        });

        group.MapPost("/jobs/{id}/progress", (string id, int? attempt, AgentJobProgress request, HttpContext context, AgentJobStore jobs) =>
        {
            if (request.State is not ("queued" or "downloading" or "installing" or "cancelled") || request.Percent is < 0 or > 100)
            {
                return Results.BadRequest(new ErrorMessage("A valid progress state and percent from 0 to 100 are required."));
            }

            var device = DeviceAuthenticationMiddleware.Current(context);
            return jobs.Progress(device.Id, id, request, attempt) ? Results.NoContent() : Results.Conflict(new ErrorMessage("The job has no current lease for this device."));
        });

        group.MapPost("/jobs/{id}/complete", (string id, int? attempt, AgentJobCompletion request, HttpContext context, AgentJobStore jobs) =>
        {
            var device = DeviceAuthenticationMiddleware.Current(context);
            return jobs.Complete(device.Id, id, request, attempt) ? Results.NoContent() : Results.Conflict(new ErrorMessage("The job has no current lease for this device."));
        });
    }
}
