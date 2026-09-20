using System.Security.Claims;

using AppPortal.Server.Admin;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class LogoutModel(AdminSessionStore sessions) : PageModel
{
    public IActionResult OnGet() => Redirect("/admin");

    public async Task<IActionResult> OnPostAsync()
    {
        // Delete the row as well as the cookie: the cookie alone would stay valid if it were copied.
        sessions.Revoke(User.FindFirst(AdminAuth.SessionTokenClaim)?.Value);
        await HttpContext.SignOutAsync(AdminAuth.CookieScheme);
        return Redirect("/admin/login");
    }
}
