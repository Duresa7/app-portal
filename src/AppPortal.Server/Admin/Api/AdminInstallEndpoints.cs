using AppPortal.Server.Installs;
using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>The fleet install history, with the filters the installs page offers.</summary>
public static class AdminInstallEndpoints
{
    public static void MapAdminInstallEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/installs", (HttpContext context, InstallStore installs) =>
        {
            // InstallFilter reads its own field names off the query string, so device, app, state,
            // requester, from, to and restart mean here exactly what they mean on the page.
            var filter = InstallFilter.Read(context.Request.FilterValues());
            var query = context.Request.ReadSlice();
            return Results.Ok(AdminApi.Page(installs.List(filter, query), query, Project));
        });

        group.MapGet("/installs/{id}", (string id, InstallStore installs) =>
            installs.Find(id) is { } record
                ? Results.Ok(Project(record))
                : AdminApi.NotFound("No such install."));

        group.MapPost("/installs/{id}/cancel", (string id, HttpContext context, InstallService service) =>
        {
            try
            {
                return Results.Ok(Project(service.Cancel(id, context.Username())));
            }
            catch (InstallRejectedException ex)
            {
                return ex.Reason == InstallRejection.UnknownApp
                    ? AdminApi.NotFound(ex.Message)
                    : AdminApi.Conflict(ex.Message);
            }
        });
    }

    internal static AdminInstall Project(InstallRecord record) => new(
        record.Id,
        record.AppId,
        record.AppName,
        record.DeviceId,
        record.DeviceName,
        record.EndpointId,
        record.RequestedBy,
        record.Engine,
        record.Kind,
        record.State,
        record.PercentComplete,
        record.Detail,
        record.RebootState,
        record.StepName,
        record.StepNumber,
        record.StepCount,
        record.RequestedAt,
        record.CompletedAt,
        record.LastCheckedAt,
        record.AutomationId);
}
