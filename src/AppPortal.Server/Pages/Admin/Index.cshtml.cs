using AppPortal.Server.Admin;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(DeviceStore devices, InstallStore installs) : PageModel
{
    public int Devices { get; private set; }

    public int InstallsToday { get; private set; }

    /// <summary>Always zero until M1-04 creates requests; the tile exists so the layout does not move.</summary>
    public int PendingRequests => 0;

    public void OnGet()
    {
        Devices = devices.All().Count;

        var since = DateTimeOffset.UtcNow.Date;
        InstallsToday = installs.All().Count(i => i.RequestedAt.UtcDateTime >= since);
    }
}
