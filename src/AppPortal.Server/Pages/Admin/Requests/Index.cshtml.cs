using AppPortal.Server.Admin;
using AppPortal.Server.Requests;
using AppPortal.Shared;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin.Requests;

/// <summary>One row of the table. Tab and page ride along so the no-script submit comes back where it left.</summary>
public sealed record RequestRowView(AppRequestRecord Request, string Tab, int PageNumber);

/// <summary>
/// The table, and what an htmx response has to carry besides it: the pending count for the navigation
/// badge and whatever the decision had to say. Both ride along as out-of-band swaps, which is why the
/// flag exists; rendering them inside the full page would put a second badge on the screen.
/// </summary>
public sealed record RequestTableView(
    IReadOnlyList<AppRequestRecord> Requests,
    string Tab,
    int PageNumber,
    bool HasNextPage,
    int PendingCount,
    string? Message,
    bool MessageIsError,
    bool OutOfBand);

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(AppRequestStore requests, IAdminContext current) : PageModel
{
    public const int PageSize = 50;

    /// <summary>The tabs, in the order they are shown. The key is what the query string carries.</summary>
    public static readonly (string Key, string Label, AppRequestStatus? Status)[] Tabs =
    [
        ("pending", "Pending", AppRequestStatus.Pending),
        ("approved", "Approved", AppRequestStatus.Approved),
        ("denied", "Denied", AppRequestStatus.Denied),
        ("all", "All", null),
    ];

    public IReadOnlyList<AppRequestRecord> Items { get; private set; } = [];

    public string Tab { get; private set; } = "pending";

    public int PageNumber { get; private set; } = 1;

    public bool HasNextPage { get; private set; }

    public int PendingCount { get; private set; }

    public string? Notice { get; private set; }

    public string? Error { get; private set; }

    // The page number travels as "p", not "page": Razor Pages already owns the route value "page"
    // and binds it to the page's own path, so a parameter of that name silently arrives null.
    public void OnGet(string? tab, int? p) => Load(tab, p);

    public IActionResult OnPostApprove(string id, string? reason, string? tab, int? p)
        => Decide(id, AppRequestStatus.Approved, reason, tab, p);

    public IActionResult OnPostDeny(string id, string? reason, string? tab, int? p)
        => Decide(id, AppRequestStatus.Denied, reason, tab, p);

    private IActionResult Decide(string id, AppRequestStatus status, string? reason, string? tab, int? page)
    {
        var trimmed = (reason ?? "").Trim();
        if (trimmed.Length > AppRequestLimits.MaxTextLength)
        {
            Error = $"A reason may be at most {AppRequestLimits.MaxTextLength} characters.";
        }
        else if (requests.Decide(id ?? "", status, trimmed.Length == 0 ? null : trimmed, current.Username ?? "unknown"))
        {
            Notice = status == AppRequestStatus.Approved ? "Request approved." : "Request denied.";
        }
        else
        {
            // Decide only writes to a pending row, so a false here means somebody else got there first.
            // The user sees whatever that administrator decided, not a second decision on top of it.
            Error = "That request had already been decided. The table shows the decision that was recorded.";
        }

        Load(tab, page);

        if (!Request.Headers.ContainsKey("HX-Request"))
        {
            return Page();
        }

        return Partial("_RequestTable", new RequestTableView(
            Items, Tab, PageNumber, HasNextPage, PendingCount, Error ?? Notice, MessageIsError: Error is not null, OutOfBand: true));
    }

    private void Load(string? tab, int? page)
    {
        Tab = Tabs.Any(t => t.Key == tab) ? tab! : "pending";
        PageNumber = page is > 1 ? page.Value : 1;

        var status = Tabs.First(t => t.Key == Tab).Status;

        // One row past the page is read and then dropped, which is what tells the pager there is a
        // next page without a second COUNT query over the same rows.
        var window = requests.ListByStatus(status, PageSize + 1, (PageNumber - 1) * PageSize);
        HasNextPage = window.Count > PageSize;
        Items = HasNextPage ? [.. window.Take(PageSize)] : window;
        PendingCount = requests.PendingCount();
    }
}
