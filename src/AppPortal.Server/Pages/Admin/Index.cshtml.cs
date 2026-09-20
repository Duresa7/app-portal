using AppPortal.Server.Admin;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Server.Requests;
using AppPortal.Shared;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(DeviceStore devices, InstallStore installs, AppRequestStore requests) : PageModel
{
    public int Devices { get; private set; }

    public int InstallsToday { get; private set; }

    public int FailuresThisWeek { get; private set; }

    public int ActiveNow { get; private set; }

    public int PendingRequests { get; private set; }

    /// <summary>
    /// Midnight where the server is, not where UTC is. Every time on these pages is rendered local, so
    /// "today" has to mean the same day the table shows, or the tile disagrees with the rows under it.
    /// </summary>
    public static DateTimeOffset TodayStarted => new(DateTime.Today, DateTimeOffset.Now.Offset);

    public void OnGet()
    {
        Devices = devices.All().Count;
        InstallsToday = installs.CountBy(null, TodayStarted);
        FailuresThisWeek = installs.CountBy(InstallState.Failed, DateTimeOffset.Now.AddDays(-7));
        ActiveNow = installs.CountActive();
        PendingRequests = requests.PendingCount();
    }
}
