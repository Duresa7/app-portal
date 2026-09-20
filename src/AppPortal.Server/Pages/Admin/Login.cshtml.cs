using AppPortal.Server.Admin;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin;

public sealed class LoginModel(
    AdminStore admins,
    AdminSessionStore sessions,
    SignInThrottle throttle,
    ILogger<LoginModel> logger) : PageModel
{
    [BindProperty]
    public string Username { get; set; } = "";

    [BindProperty]
    public string Password { get; set; } = "";

    public string? Error { get; private set; }

    public IActionResult OnGet()
        => User.Identity?.IsAuthenticated == true ? Redirect("/admin") : Page();

    public async Task<IActionResult> OnPostAsync()
    {
        var username = (Username ?? "").Trim();

        // Checked before the password is, so guessing costs the attacker the wait either way.
        if (throttle.IsBlocked(username))
        {
            logger.LogWarning("Sign-in for {Username} refused: too many recent failures", username);
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            Error = $"Too many failed attempts. Wait {(int)SignInThrottle.Window.TotalMinutes} minutes and try again.";
            return Page();
        }

        var admin = admins.Verify(username, Password ?? "");
        if (admin is null)
        {
            throttle.RecordFailure(username);
            logger.LogInformation("Sign-in failed for {Username}", username);

            // One message for a wrong name, a wrong password and a disabled account alike.
            Error = "That user name and password do not match an enabled administrator.";
            return Page();
        }

        throttle.Clear(username);
        var token = sessions.Create(admin.Id, AdminSessionKind.Web);
        admins.RecordLogin(admin.Id);
        await HttpContext.SignInAsync(AdminAuth.CookieScheme, AdminPrincipal.For(admin, token, AdminAuth.CookieScheme));
        logger.LogInformation("Administrator {Username} signed in", admin.Username);
        return Redirect("/admin");
    }
}
