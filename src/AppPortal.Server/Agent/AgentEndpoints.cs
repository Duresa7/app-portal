using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
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

    /// <summary>
    /// Every manager the agent knows, once machine-wide and once for each of a few signed-in accounts.
    /// Far more than a real PC reports, and far less than a device could use to fill the table.
    /// </summary>
    private const int MaxManagerEntries = 256;

    public static void MapAgentApi(this WebApplication app)
    {
        var group = app.MapGroup(ApiRoutes.Prefix + "/agent");
        group.MapPost("/heartbeat", (AgentHeartbeatRequest request, HttpContext context, DeviceStore devices,
            IConfiguration configuration, AgentJobStore jobs, RestartConfirmation restarts) =>
        {
            if (string.IsNullOrWhiteSpace(request.AgentVersion) || string.IsNullOrWhiteSpace(request.OsVersion))
            {
                return Results.BadRequest(new ErrorMessage("Agent and OS versions are required."));
            }

            var device = DeviceAuthenticationMiddleware.Current(context);
            devices.RecordHeartbeat(device.Id, request.AgentVersion);
            if (request.BootTime is { } booted)
            {
                // Anything that was waiting for a restart before this boot has had one.
                restarts.Apply(device, booted);
            }

            var seconds = jobs.HasQueued(device.Id) ? 60 : configuration.GetValue("Agent:HeartbeatSeconds", 900);
            return Results.Ok(new AgentHeartbeatResponse(DateTimeOffset.UtcNow, seconds > 0 ? seconds : 900));
        });

        group.MapGet("/jobs", async (int? wait, HttpContext context, AgentJobStore jobs, IHostApplicationLifetime lifetime, CancellationToken ct) =>
        {
            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.ApplicationStopping);
            var token = stopping.Token;
            var device = DeviceAuthenticationMiddleware.Current(context);

            // Whoever is signed in right now. A per-user install parks until the person who asked for
            // it is at the PC, and this header is how the agent says they have arrived. Sent on every
            // poll rather than only on a change, so a missed sign-in cannot strand a job.
            var signedIn = (context.Request.Headers[ApiHeaders.SignedInAccounts].ToString() ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(account => account.Length <= ApiHeaders.RequesterMaxLength)
                .ToArray();
            if (signedIn.Length > 0)
            {
                jobs.Resume(device.Id, signedIn);
            }

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

        group.MapPost("/software", (IReadOnlyList<InstalledSoftware> request, string? account, string? source, HttpContext context,
            DeviceSoftwareStore software) =>
        {
            // The whole list, every time. A device that had software removed has to be able to say so,
            // and a merge would leave anything the agent stopped reporting on the record for ever.
            var device = DeviceAuthenticationMiddleware.Current(context);
            if (request.Count > MaxSoftwareEntries)
            {
                return Results.BadRequest(new ErrorMessage($"A device may report at most {MaxSoftwareEntries} pieces of software."));
            }

            if (account is { Length: > ApiHeaders.RequesterMaxLength })
            {
                return Results.BadRequest(new ErrorMessage("That account name is too long."));
            }

            // No source is winget, which is all an agent from before package managers ever sends, so it
            // keeps replacing exactly the rows it always did.
            source = string.IsNullOrWhiteSpace(source) ? DeviceSoftwareStore.WingetSource : source.Trim();
            if (source != DeviceSoftwareStore.WingetSource && PackageManagers.Find(source) is null)
            {
                return Results.BadRequest(new ErrorMessage($"This server does not know the software source '{source}'."));
            }

            // No account is the machine-wide sweep; an account is one profile's own software. They are
            // separate lists, and so is each source, so one sweep never erases another.
            software.Replace(device.Id, request, account, source);
            return Results.NoContent();
        });

        group.MapPost("/managers", (IReadOnlyList<DeviceManager> request, HttpContext context, DeviceManagerStore managers) =>
        {
            // Every manager the agent found, every time, so one that was removed leaves the record.
            var device = DeviceAuthenticationMiddleware.Current(context);
            if (request.Count > MaxManagerEntries)
            {
                return Results.BadRequest(new ErrorMessage($"A device may report at most {MaxManagerEntries} package managers."));
            }

            if (request.Any(m => m.Account is { Length: > ApiHeaders.RequesterMaxLength }))
            {
                return Results.BadRequest(new ErrorMessage("That account name is too long."));
            }

            managers.Replace(device.Id, request);
            return Results.NoContent();
        });

        group.MapPost("/jobs/{id}/progress", (string id, int? attempt, AgentJobProgress request, HttpContext context, AgentJobStore jobs) =>
        {
            if (request.State is not ("queued" or "downloading" or "installing" or "waiting_for_user" or "cancelled")
                || request.Percent is < 0 or > 100)
            {
                return Results.BadRequest(new ErrorMessage("A valid progress state and percent from 0 to 100 are required."));
            }

            var device = DeviceAuthenticationMiddleware.Current(context);
            // A report the store chose to ignore, such as one that moves the state backwards, leaves the
            // job exactly as it was. That is not a conflict, and answering it as one makes the agent
            // abandon a run it is carrying out correctly.
            return jobs.Progress(device.Id, id, request, attempt) is JobUpdate.NotLeased
                ? Results.Conflict(new ErrorMessage("The job has no current lease for this device."))
                : Results.NoContent();
        });

        group.MapPost("/jobs/{id}/complete", (string id, int? attempt, AgentJobCompletion request, HttpContext context, AgentJobStore jobs) =>
        {
            var device = DeviceAuthenticationMiddleware.Current(context);
            return jobs.Complete(device.Id, id, request, attempt) ? Results.NoContent() : Results.Conflict(new ErrorMessage("The job has no current lease for this device."));
        });
    }
}
