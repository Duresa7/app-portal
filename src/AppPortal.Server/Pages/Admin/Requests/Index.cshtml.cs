using AppPortal.Server.Admin;
using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Requests;
using AppPortal.Shared;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppPortal.Server.Pages.Admin.Requests;

/// <summary>One row of the table. Tab and page ride along so the no-script submit comes back where it left.</summary>
public sealed record RequestRowView(AppRequestRecord Request, string Tab, int PageNumber);

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(AppRequestStore requests, IAdminContext current) : AdminListPage<RequestFilter, AppRequestRecord>
{
    public const int PageSize = 50;

    // The page number travels as "p", not "page": Razor Pages already owns the route value "page"
    // and binds it to the page's own path, so a parameter of that name silently arrives null.
    // No total: the pager only says whether a next page exists, which one row of lookahead answers.
    private static readonly Paging Pages = Paging.ByPages("p", PageSize);

    /// <summary>The tabs, in the order they are shown, each with the filter it stands for.</summary>
    public static readonly (RequestFilter Filter, string Label)[] Tabs =
    [
        (RequestFilter.Pending, "Pending"),
        (new RequestFilter(AppRequestStatus.Approved), "Approved"),
        (new RequestFilter(AppRequestStatus.Denied), "Denied"),
        (RequestFilter.Everything, "All"),
    ];

    public int PendingCount { get; private set; }

    public override Paging Paging => Pages;

    protected override string TablePartial => "_RequestTable";

    public IActionResult OnPostApprove(string id, string? reason)
        => Decide(id, AppRequestStatus.Approved, reason);

    public IActionResult OnPostDeny(string id, string? reason)
        => Decide(id, AppRequestStatus.Denied, reason);

    private IActionResult Decide(string id, AppRequestStatus status, string? reason)
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

        return Done();
    }

    protected override void Load()
    {
        Slice = requests.List(Filter, Query);
        PendingCount = requests.PendingCount();
    }
}
