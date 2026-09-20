using AppPortal.Server.Admin;
using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Options;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppPortal.Server.Pages.Admin;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class AdminsModel(
    AdminStore admins,
    AdminSessionStore sessions,
    IAdminContext current,
    Microsoft.Extensions.Options.IOptions<DirectoryOptions> directory) : AdminListPage<NoFilter, AdminRecord>
{
    public bool DirectoryEnabled => directory.Value.Enabled;

    public string DirectoryGroup => directory.Value.RequiredGroup;

    [BindProperty]
    public string NewUsername { get; set; } = "";

    [BindProperty]
    public string NewPassword { get; set; } = "";

    [BindProperty]
    public string ResetUsername { get; set; } = "";

    [BindProperty]
    public string ResetPassword { get; set; } = "";

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

    protected override string TablePartial => "Shared/_AdminTable";

    protected override void Load() => Slice = admins.List(Filter, Query);
}
