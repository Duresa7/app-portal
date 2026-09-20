using AppPortal.Server.Admin;
using AppPortal.Server.Enrollment;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin.Keys;

/// <summary>
/// One key and what it has let in. The list page says how many uses a key has spent; this says which
/// machines spent them and which attempts were turned away, which is the question asked when a key
/// has been handed around more widely than intended.
/// </summary>
[Authorize(Policy = AdminAuth.Policy)]
public sealed class DetailModel(EnrollmentKeyStore keys, EnrollmentEventStore events) : PageModel
{
    public EnrollmentKeyRecord Key { get; private set; } = null!;

    public IReadOnlyList<EnrollmentEventRecord> Events { get; private set; } = [];

    public string? Notice { get; private set; }

    public IActionResult OnGet(string id) => Load(id) ? Page() : NotFound();

    public IActionResult OnPostRevoke(string id)
    {
        if (!Load(id))
        {
            return NotFound();
        }

        Notice = keys.Revoke(Key.Id)
            ? "That key is revoked. Machines that have not enrolled with it yet no longer can."
            : "That key was already revoked.";

        Load(id);
        return Page();
    }

    private bool Load(string id)
    {
        var record = keys.Find(id ?? "");
        if (record is null)
        {
            return false;
        }

        Key = record;
        Events = events.ForKey(record.Id);
        return true;
    }
}
