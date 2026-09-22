using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Devices;
using AppPortal.Server.Enrollment;
using AppPortal.Server.Installs;
using AppPortal.Server.Requests;
using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>The fleet, as the device list and the device page work it.</summary>
public static class AdminDeviceEndpoints
{
    /// <summary>How much recent history the device page shows, and so how much this endpoint returns.</summary>
    private const int RecentRows = 20;

    public static void MapAdminDeviceEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/devices", (HttpContext context, DeviceStore devices, EnrollmentKeyStore keys) =>
        {
            var filter = SearchFilter.Read(context.Request.FilterValues());
            var query = context.Request.ReadSlice();
            var counts = devices.InstallCounts();
            var keyNames = keys.List().ToDictionary(key => key.Id, key => key.Name);
            return Results.Ok(AdminApi.Page(devices.List(filter, query), query, d => Project(d, counts, keyNames)));
        });

        group.MapGet("/devices/{id}", (
            string id,
            DeviceStore devices,
            EnrollmentKeyStore keys,
            InstallStore installs,
            AppRequestStore requests,
            DeviceManagerStore managers) =>
        {
            if (devices.Find(id) is not { } device)
            {
                return AdminApi.NotFound("No such device.");
            }

            // The recent history comes back with the device because the device page shows it there and
            // there is no other way to ask for one device's requests; the requests filter has no device.
            var keyNames = keys.List().ToDictionary(key => key.Id, key => key.Name);
            return Results.Ok(new AdminDeviceDetail(
                Project(device, devices.InstallCounts(), keyNames),
                [.. installs.ForDeviceId(device.Id).Take(RecentRows).Select(AdminInstallEndpoints.Project)],
                [.. requests.ListForDeviceId(device.Id).Take(RecentRows).Select(AdminRequestEndpoints.Project)],
                managers.ForDevice(device.Id)));
        });

        group.MapPost("/devices", (AdminDeviceCreate body, DeviceStore devices) =>
        {
            var name = (body?.Name ?? "").Trim();
            if (name.Length == 0)
            {
                return AdminApi.BadRequest("A device needs a name.");
            }

            if (devices.FindByName(name) is not null)
            {
                // Add would take the row over and rotate its token. That is right for a re-enrolment and
                // wrong for somebody adding what they believe is a new PC, so the page refuses it too.
                return AdminApi.Conflict($"A device called '{name}' is already registered. Rotate its token instead.");
            }

            var token = devices.Add(name, (body!.Action1EndpointId ?? "").Trim());
            var created = devices.FindByName(name)!;
            return Results.Created($"{AdminApiRoutes.Devices}/{created.Id}", new AdminDeviceToken(created.Id, token));
        });

        group.MapPut("/devices/{id}", (string id, AdminDeviceUpdate body, DeviceStore devices, EnrollmentKeyStore keys) =>
        {
            if (devices.Find(id) is not { } device)
            {
                return AdminApi.NotFound("No such device.");
            }

            try
            {
                devices.Update(new DeviceRecord
                {
                    Id = device.Id,
                    Name = body?.Name ?? "",
                    EndpointId = body?.Action1EndpointId ?? "",
                    Enabled = body?.Enabled ?? true,
                    EnginePreference = string.IsNullOrWhiteSpace(body?.EnginePreference) ? null : body!.EnginePreference,
                });
            }
            catch (DeviceRejectedException ex)
            {
                return AdminApi.Conflict(ex.Message);
            }

            var keyNames = keys.List().ToDictionary(key => key.Id, key => key.Name);
            return Results.Ok(Project(devices.Find(id)!, devices.InstallCounts(), keyNames));
        });

        group.MapPost("/devices/{id}/rotate-token", (string id, DeviceStore devices) =>
            devices.RotateToken(id) is { } token
                ? Results.Ok(new AdminDeviceToken(id, token))
                : AdminApi.NotFound("No such device."));

        group.MapDelete("/devices/{id}", (string id, DeviceStore devices) =>
        {
            if (devices.Find(id) is null)
            {
                return AdminApi.NotFound("No such device.");
            }

            return devices.RemoveById(id)
                ? Results.NoContent()
                : AdminApi.Conflict("This device has an install still running. Wait for it to finish, or disable the device instead.");
        });
    }

    private static AdminDevice Project(
        DeviceRecord device,
        IReadOnlyDictionary<string, int> installCounts,
        IReadOnlyDictionary<string, string> keyNames)
        => new(
            device.Id,
            device.Name,
            device.EndpointId,
            device.Enabled,
            device.HasAgent,
            device.EnginePreference,
            device.AgentVersion,
            device.EnrolledWithKeyId,
            device.EnrolledWithKeyId is { } key && keyNames.TryGetValue(key, out var name) ? name : null,
            device.MachineId,
            device.CreatedAt,
            device.LastSeenAt,
            installCounts.TryGetValue(device.Id, out var count) ? count : 0);
}
