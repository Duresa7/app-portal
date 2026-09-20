using System.Globalization;

using AppPortal.Server.Admin;
using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Enrollment;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppPortal.Server.Pages.Admin.Keys;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(EnrollmentKeyStore keys, IAdminContext current) : AdminListPage<NoFilter, EnrollmentKeyRecord>
{
    /// <summary>
    /// The plaintext of a key created by this very request. Set on no other path: the database holds
    /// only a hash, so once this response is gone the key cannot be shown again by anyone.
    /// </summary>
    public string? CreatedPlaintext { get; private set; }

    [BindProperty]
    public string NewName { get; set; } = "";

    [BindProperty]
    public string NewExpires { get; set; } = "";

    [BindProperty]
    public string NewMaxUses { get; set; } = "";

    [BindProperty]
    public string NewEngine { get; set; } = "action1";

    public IActionResult OnPostCreate()
    {
        try
        {
            var created = keys.Create(
                NewName ?? "",
                EnrollmentKeyStore.ParseEngine(NewEngine),
                ParseExpiry(NewExpires),
                ParseMaxUses(NewMaxUses),
                current.Username ?? "unknown");

            CreatedPlaintext = created.Plaintext;
            Notice = $"Key '{created.Key.Name}' created. Copy it now: it is not stored and cannot be shown again.";
            NewName = "";
            NewExpires = "";
            NewMaxUses = "";
        }
        catch (EnrollmentKeyRejectedException ex)
        {
            Error = ex.Message;
        }

        Load();

        // Deliberately not a redirect. The plaintext exists only in this response, and a redirect would
        // throw it away before the administrator could copy it.
        return Page();
    }

    public IActionResult OnPostRevoke(string id)
    {
        Notice = keys.Revoke(id ?? "")
            ? "That key is revoked. Machines that have not enrolled with it yet no longer can."
            : "That key was already revoked.";
        return Done();
    }

    protected override string TablePartial => "_KeyTable";

    protected override void Load() => Slice = keys.List(Filter, Query);

    /// <summary>
    /// A bare date means the end of that day in UTC, not midnight at its start: someone typing
    /// today's date means "good for the rest of today", not "already expired".
    /// </summary>
    private static DateTimeOffset? ParseExpiry(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new DateTimeOffset(date.Date.AddDays(1).AddTicks(-1), TimeSpan.Zero);
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        throw new EnrollmentKeyRejectedException($"'{trimmed}' is not a date. Write it as YYYY-MM-DD.");
    }

    private static int? ParseMaxUses(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            ? max
            : throw new EnrollmentKeyRejectedException($"'{trimmed}' is not a whole number of uses.");
    }
}
