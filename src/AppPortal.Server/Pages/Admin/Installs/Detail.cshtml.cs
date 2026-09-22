using AppPortal.Server.Admin;
using AppPortal.Server.Installs;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin.Installs;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class DetailModel(InstallStore installs, InstallService service) : PageModel
{
    public InstallRecord Install { get; private set; } = null!;

    public string? Error { get; private set; }

    public bool Stopped { get; private set; }

    public IActionResult OnGet(string id, bool stopped = false)
    {
        Stopped = stopped;
        return Load(id);
    }

    public IActionResult OnPostCancel(string id)
    {
        try
        {
            service.Cancel(id, User.Identity?.Name ?? "an administrator");
        }
        catch (InstallRejectedException ex)
        {
            Error = ex.Message;
            return Load(id);
        }

        return RedirectToPage(new { id, stopped = true });
    }

    private IActionResult Load(string id)
    {
        var record = installs.Find(id);
        if (record is null)
        {
            return NotFound();
        }

        Install = record;
        return Page();
    }
}
