using AppPortal.Server.Admin;
using AppPortal.Server.Installs;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin.Installs;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class DetailModel(InstallStore installs) : PageModel
{
    public InstallRecord Install { get; private set; } = null!;

    public IActionResult OnGet(string id)
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
