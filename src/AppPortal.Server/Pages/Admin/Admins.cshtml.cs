using AppPortal.Server.Admin;
using AppPortal.Server.Options;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class AdminsModel(
    AdminStore admins,
    AdminSessionStore sessions,
    IAdminContext current,
    Microsoft.Extensions.Options.IOptions<DirectoryOptions> directory) : PageModel
{
    public IReadOnlyList<AdminRecord> Admins { get; private set; } = [];

    public bool DirectoryEnabled => directory.Value.Enabled;

    public string DirectoryGroup => directory.Value.RequiredGroup;

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    [BindProperty]
    public string NewUsername { get; set; } = "";

    [BindProperty]
    public string NewPassword { get; set; } = "";

    [BindProperty]
    public string ResetUsername { get; set; } = "";

    [BindProperty]
    public string ResetPassword { get; set; } = "";

    public void OnGet() => Load();

    public IActionResult OnPostAdd()
    {
        try
        {
            admins.Add(NewUsername ?? "", NewPassword ?? "");
            Notice = $"Administrator '{(NewUsername ?? "").Trim()}' added.";
        }
        catch (AdminRejectedException ex)
        {
            Error = ex.Message;
        }

        return Done();
    }

    public IActionResult OnPostReset()
    {
        try
        {
            admins.SetPassword(ResetUsername ?? "", ResetPassword ?? "");
            var record = admins.Find(ResetUsername ?? "");
            if (record is not null)
            {
                sessions.RevokeAllFor(record.Id);
            }

            Notice = $"Password changed for '{(ResetUsername ?? "").Trim()}'. That account is signed out everywhere.";
        }
        catch (AdminRejectedException ex)
        {
            Error = ex.Message;
        }

        return Done();
    }

    public IActionResult OnPostDisable(string username)
    {
        try
        {
            var record = admins.Find(username ?? "");
            if (record is not null && record.Id == current.AdminId)
            {
                // Signing yourself out by disabling yourself is a support call waiting to happen.
                throw new AdminRejectedException("You cannot disable the account you are signed in with.");
            }

            admins.SetDisabled(username ?? "", true);
            if (record is not null)
            {
                sessions.RevokeAllFor(record.Id);
            }

            Notice = $"Administrator '{username}' is disabled and signed out.";
        }
        catch (AdminRejectedException ex)
        {
            Error = ex.Message;
        }

        return Done();
    }

    private void Load() => Admins = admins.All();

    /// <summary>
    /// htmx asked for the table alone, so give it the table alone; a plain browser post gets the page.
    /// </summary>
    private IActionResult Done()
    {
        Load();
        return Request.Headers.ContainsKey("HX-Request")
            ? Partial("Shared/_AdminTable", Admins)
            : Page();
    }
}
