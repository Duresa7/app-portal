using AppPortal.Server.Action1;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Server.Requests;
using AppPortal.Shared;

namespace AppPortal.Server.Api;

public static class PortalEndpoints
{
    public static IEndpointRouteBuilder MapPortalApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(ApiRoutes.Prefix);

        // Hidden apps are withheld here rather than deleted, so a device stops being offered an app
        // the moment an administrator hides it while its install history stays intact.
        api.MapGet("/catalog", (CatalogStore catalog) =>
            Results.Ok(catalog.VisibleEntries.Select(e => e.ToPublic()).ToList()));

        api.MapGet("/device", async (HttpContext context, IAction1Client action1, CancellationToken ct) =>
        {
            var device = DeviceAuthenticationMiddleware.Current(context);
            Action1Endpoint? endpoint = null;
            try
            {
                endpoint = await action1.GetEndpointAsync(device.EndpointId, ct);
            }
            catch (Action1Exception)
            {
                // The device page still renders without live agent data.
            }

            return Results.Ok(new DeviceInfo(device.Name, device.EndpointId, endpoint?.Status ?? "Unknown", endpoint?.LastSeen));
        });

        api.MapGet("/device/installed", async (HttpContext context, InstallService installs, CancellationToken ct) =>
        {
            var device = DeviceAuthenticationMiddleware.Current(context);
            try
            {
                // Their own per-user software as well as the device's, and nobody else's.
                return Results.Ok(await installs.InstalledAppsAsync(device, ct, DeviceAuthenticationMiddleware.RequestedBy(context)));
            }
            catch (Action1Exception ex)
            {
                return Results.Json(new ErrorMessage(ex.Message), statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapGet("/installs", async (HttpContext context, InstallService installs, bool? refresh, CancellationToken ct) =>
        {
            var device = DeviceAuthenticationMiddleware.Current(context);
            var records = await installs.ListForDeviceAsync(device, refresh ?? true, ct);
            return Results.Ok(records.Select(r => r.ToPublic()).ToList());
        });

        api.MapGet("/installs/{id}", async (HttpContext context, string id, InstallStore store, InstallService installs, CancellationToken ct) =>
        {
            var device = DeviceAuthenticationMiddleware.Current(context);
            var record = store.Find(id);
            if (record is null || !string.Equals(record.DeviceId, device.Id, StringComparison.Ordinal))
            {
                return Results.NotFound(new ErrorMessage("No such install request."));
            }

            record = await installs.RefreshAsync(record, ct);
            return Results.Ok(record.ToPublic());
        });

        api.MapPost("/installs", async (HttpContext context, CreateInstallRequest body, InstallService installs, CancellationToken ct) =>
        {
            var device = DeviceAuthenticationMiddleware.Current(context);
            if (string.IsNullOrWhiteSpace(body.AppId))
            {
                return Results.BadRequest(new ErrorMessage("appId is required."));
            }

            try
            {
                var record = await installs.CreateAsync(device, body.AppId, DeviceAuthenticationMiddleware.RequestedBy(context), ct);
                return Results.Accepted($"{ApiRoutes.Installs}/{record.Id}", record.ToPublic());
            }
            catch (InstallRejectedException ex)
            {
                var status = ex.Reason switch
                {
                    InstallRejection.UnknownApp => StatusCodes.Status404NotFound,
                    InstallRejection.AlreadyInProgress => StatusCodes.Status409Conflict,
                    InstallRejection.TooManyActive => StatusCodes.Status429TooManyRequests,
                    _ => StatusCodes.Status422UnprocessableEntity,
                };
                return Results.Json(new ErrorMessage(ex.Message), statusCode: status);
            }
            catch (Action1Exception ex)
            {
                return Results.Json(new ErrorMessage("The management service refused the request: " + ex.Message), statusCode: StatusCodes.Status502BadGateway);
            }
        });

        api.MapGet("/requests", (HttpContext context, AppRequestStore requests) =>
        {
            var device = DeviceAuthenticationMiddleware.Current(context);
            return Results.Ok(requests.ListForDeviceId(device.Id).Select(r => r.ToPublic()).ToList());
        });

        api.MapPost("/requests", (HttpContext context, CreateAppRequest body, AppRequestStore requests, ILoggerFactory loggers) =>
        {
            var device = DeviceAuthenticationMiddleware.Current(context);
            try
            {
                var record = requests.CreateForDeviceId(device.Id, DeviceAuthenticationMiddleware.RequestedBy(context), body?.Text ?? "");
                loggers.CreateLogger("AppPortal.Server.Requests")
                    .LogInformation("Device {Device} asked for {Text}", device.Name, record.Text);
                return Results.Created($"{ApiRoutes.Requests}/{record.Id}", record.ToPublic());
            }
            catch (AppRequestRejectedException ex)
            {
                var status = ex.Reason switch
                {
                    AppRequestRejection.TooManyPending => StatusCodes.Status429TooManyRequests,
                    _ => StatusCodes.Status400BadRequest,
                };
                return Results.Json(new ErrorMessage(ex.Message), statusCode: status);
            }
        });

        return app;
    }
}
