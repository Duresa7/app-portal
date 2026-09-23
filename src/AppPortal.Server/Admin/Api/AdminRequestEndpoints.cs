using AppPortal.Server.Catalog;
using AppPortal.Server.Requests;
using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>Software requests, the two decisions an administrator can make about one, and the app that answers it.</summary>
public static class AdminRequestEndpoints
{
    public static void MapAdminRequestEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/requests", (HttpContext context, AppRequestStore requests) =>
        {
            var status = context.Request.Query["status"].FirstOrDefault();
            if (!TryFilter(status, out var filter))
            {
                return AdminApi.BadRequest("status must be pending, approved, denied or all.");
            }

            var query = context.Request.ReadSlice();
            return Results.Ok(AdminApi.Page(requests.List(filter, query), query, Project));
        });

        group.MapPost("/requests/{id}/approve", (string id, AdminDecision? body, HttpContext context, AppRequestStore requests, CatalogStore catalog) =>
            Decide(id, AppRequestStatus.Approved, body, context, requests, catalog));

        group.MapPost("/requests/{id}/deny", (string id, AdminDecision? body, HttpContext context, AppRequestStore requests, CatalogStore catalog) =>
            Decide(id, AppRequestStatus.Denied, body, context, requests, catalog));

        group.MapPut("/requests/{id}/catalog-app", (string id, AdminRequestLink? body, AppRequestStore requests) =>
        {
            if (body is null)
            {
                return AdminApi.BadRequest("Send the catalog app id, or null to remove the link.");
            }

            return requests.Link(id, body.CatalogAppId) switch
            {
                RequestLinkResult.Linked => Results.Ok(Project(requests.Find(id)!)),
                RequestLinkResult.NoSuchRequest => AdminApi.NotFound("No such request."),
                RequestLinkResult.NotApproved => AdminApi.Conflict("Only an approved request can name a catalog app."),
                _ => NoSuchApp(body.CatalogAppId!),
            };
        });
    }

    /// <summary>
    /// The page opens on the pending tab because that is the queue somebody came to work through. A
    /// list endpoint asked for nothing in particular means every request, because silently answering
    /// with one status would be a filter the caller never asked for and cannot see.
    /// </summary>
    private static bool TryFilter(string? status, out RequestFilter filter)
    {
        filter = RequestFilter.Everything;
        if (string.IsNullOrWhiteSpace(status) || status.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Not RequestFilter.Read: that falls back to pending for anything it does not know, which is
        // right for a tab in a browser and wrong for an API, where a typo would quietly narrow the list.
        if (!AdminApi.TryName<AppRequestStatus>(status, out var parsed))
        {
            return false;
        }

        filter = new RequestFilter(parsed);
        return true;
    }

    private static IResult Decide(
        string id,
        AppRequestStatus status,
        AdminDecision? body,
        HttpContext context,
        AppRequestStore requests,
        CatalogStore catalog)
    {
        var trimmed = (body?.Reason ?? "").Trim();
        if (trimmed.Length > AppRequestLimits.MaxTextLength)
        {
            return AdminApi.BadRequest($"A reason may be at most {AppRequestLimits.MaxTextLength} characters.");
        }

        var named = string.IsNullOrWhiteSpace(body?.CatalogAppId) ? null : body.CatalogAppId.Trim();
        if (named is not null && status == AppRequestStatus.Denied)
        {
            return AdminApi.BadRequest("A denied request cannot name a catalog app.");
        }

        if (requests.Find(id) is null)
        {
            return AdminApi.NotFound("No such request.");
        }

        string? app = null;
        if (named is not null)
        {
            // The catalog's own spelling is what the store keeps, so the read joins on plain equality.
            if (catalog.Find(named) is not { } entry)
            {
                return NoSuchApp(named);
            }

            app = entry.Id;
        }

        if (!requests.Decide(id, status, trimmed.Length == 0 ? null : trimmed, context.Username(), app))
        {
            // Decide only writes to a pending row, so somebody else got there first. The decision that
            // was recorded stands; this caller is told rather than allowed to write over it.
            return AdminApi.Conflict("That request had already been decided.");
        }

        return Results.Ok(Project(requests.Find(id)!));
    }

    private static IResult NoSuchApp(string id)
        => AdminApi.Problem(StatusCodes.Status422UnprocessableEntity, $"No app with id '{id.Trim()}' is in the catalog.");

    internal static AdminRequest Project(AppRequestRecord record) => new(
        record.Id,
        record.Text,
        record.DeviceName,
        record.RequestedBy,
        record.Status,
        record.Reason,
        record.DecidedBy,
        record.CreatedAt,
        record.DecidedAt,
        record.CatalogAppId,
        record.CatalogAppName,
        record.CatalogAppHidden);
}
