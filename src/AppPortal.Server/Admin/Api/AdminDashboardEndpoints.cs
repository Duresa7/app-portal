using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Server.Requests;
using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>The five numbers the web dashboard shows, counted the same way it counts them.</summary>
public static class AdminDashboardEndpoints
{
    public static void MapAdminDashboardEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/dashboard", (DeviceStore devices, InstallStore installs, AppRequestStore requests) =>
            Results.Ok(new DashboardCounts(
                devices.All().Count,
                // "Today" is midnight where the server is, not where UTC is, exactly as the page has it:
                // a tile that disagreed with the rows under it would be worse than no tile.
                installs.CountBy(null, Pages.Admin.IndexModel.TodayStarted),
                installs.CountBy(InstallState.Failed, DateTimeOffset.Now.AddDays(-7)),
                installs.CountActive(),
                requests.PendingCount())));
    }
}
