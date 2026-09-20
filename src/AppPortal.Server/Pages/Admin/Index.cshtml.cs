using AppPortal.Server.Admin;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Server.Requests;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(DeviceStore devices, InstallStore installs, AppRequestStore requests) : PageModel
{
    public int Devices { get; private set; }

    public int InstallsToday { get; private set; }

    public int PendingRequests { get; private set; }

    public void OnGet()
    {
        Devices = devices.All().Count;

        var since = DateTimeOffset.UtcNow.Date;
        InstallsToday = installs.All().Count(i => i.RequestedAt.UtcDateTime >= since);
        PendingRequests = requests.PendingCount();
    }
}
