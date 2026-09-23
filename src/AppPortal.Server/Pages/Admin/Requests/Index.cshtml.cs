using AppPortal.Server.Admin;
using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Catalog;
using AppPortal.Server.Requests;
using AppPortal.Shared;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppPortal.Server.Pages.Admin.Requests;

/// <summary>One row of the table. Tab and page ride along so the no-script submit comes back where it left.</summary>
public sealed record RequestRowView(AppRequestRecord Request, string Tab, int PageNumber);

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(AppRequestStore requests, CatalogStore catalog, IAdminContext current) : AdminListPage<RequestFilter, AppRequestRecord>
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

    /// <summary>Every catalog app, hidden ones included, for the one datalist the app id fields share.</summary>
    public IReadOnlyList<(string Id, string Name)> CatalogApps { get; private set; } = [];

    public override Paging Paging => Pages;

    protected override string TablePartial => "_RequestTable";

    public IActionResult OnPostApprove(string id, string? reason, string? catalogAppId)
    {
        string? app = null;
        if (!string.IsNullOrWhiteSpace(catalogAppId))
        {
            // The store keeps the catalog's own spelling, so the id is resolved here first.
            var named = catalogAppId.Trim();
            if (catalog.Find(named) is not { } entry)
            {
                Error = $"No app with id '{named}' is in the catalog. Nothing was decided.";
                return Done();
            }

            app = entry.Id;
        }

        Decide(id, AppRequestStatus.Approved, reason, app);
        return Done();
    }

    public IActionResult OnPostDeny(string id, string? reason)
    {
        Decide(id, AppRequestStatus.Denied, reason);
        return Done();
    }

    /// <summary>
    /// Records the approval, then goes to the create form to make the app that answers it. Always a
    /// plain navigation, never an htmx swap, so the button carries no hx-post.
    /// </summary>
    public IActionResult OnPostApproveAndAdd(string id, string? reason)
    {
        if (!Decide(id, AppRequestStatus.Approved, reason))
        {
            return Done();
        }

        return Redirect("/admin/catalog/new?fromRequest=" + Uri.EscapeDataString(id));
    }

    public IActionResult OnPostLink(string id, string? catalogAppId)
        => Link(id, catalogAppId);

    public IActionResult OnPostUnlink(string id)
        => Link(id, null);

    private IActionResult Link(string id, string? catalogAppId)
    {
        switch (requests.Link(id ?? "", catalogAppId))
        {
            case RequestLinkResult.Linked:
                var linked = requests.Find(id!);
                Notice = linked?.CatalogAppName is { } name ? $"Linked to {name}." : "The request no longer names a catalog app.";
                break;
            case RequestLinkResult.NoSuchRequest:
                Error = "No such request.";
                break;
            case RequestLinkResult.NotApproved:
                Error = "Only an approved request can name a catalog app.";
                break;
            default:
                Error = $"No app with id '{(catalogAppId ?? "").Trim()}' is in the catalog.";
                break;
        }

        return Done();
    }

    /// <summary>Records the decision and sets the message. False when nothing was decided.</summary>
    private bool Decide(string id, AppRequestStatus status, string? reason, string? catalogAppId = null)
    {
        var trimmed = (reason ?? "").Trim();
        if (trimmed.Length > AppRequestLimits.MaxTextLength)
        {
            Error = $"A reason may be at most {AppRequestLimits.MaxTextLength} characters.";
            return false;
        }

        if (requests.Decide(id ?? "", status, trimmed.Length == 0 ? null : trimmed, current.Username ?? "unknown", catalogAppId))
        {
            Notice = status == AppRequestStatus.Approved ? "Request approved." : "Request denied.";
            return true;
        }

        // Decide only writes to a pending row, so a false here means somebody else got there first.
        // The user sees whatever that administrator decided, not a second decision on top of it.
        Error = "That request had already been decided. The table shows the decision that was recorded.";
        return false;
    }

    protected override void Load()
    {
        Slice = requests.List(Filter, Query);
        PendingCount = requests.PendingCount();
        CatalogApps = [.. catalog.Entries.Select(entry => (entry.Id, entry.Name))];
    }
}
