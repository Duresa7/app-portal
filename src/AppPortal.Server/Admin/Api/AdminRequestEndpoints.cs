using AppPortal.Server.Requests;
using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>Software requests and the two decisions an administrator can make about one.</summary>
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

        group.MapPost("/requests/{id}/approve", (string id, AdminDecision? body, HttpContext context, AppRequestStore requests) =>
            Decide(id, AppRequestStatus.Approved, body?.Reason, context, requests));

        group.MapPost("/requests/{id}/deny", (string id, AdminDecision? body, HttpContext context, AppRequestStore requests) =>
            Decide(id, AppRequestStatus.Denied, body?.Reason, context, requests));
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
        string? reason,
        HttpContext context,
        AppRequestStore requests)
    {
        var trimmed = (reason ?? "").Trim();
        if (trimmed.Length > AppRequestLimits.MaxTextLength)
        {
            return AdminApi.BadRequest($"A reason may be at most {AppRequestLimits.MaxTextLength} characters.");
        }

        if (requests.Find(id) is null)
        {
            return AdminApi.NotFound("No such request.");
        }

        if (!requests.Decide(id, status, trimmed.Length == 0 ? null : trimmed, context.Username()))
        {
            // Decide only writes to a pending row, so somebody else got there first. The decision that
            // was recorded stands; this caller is told rather than allowed to write over it.
            return AdminApi.Conflict("That request had already been decided.");
        }

        return Results.Ok(Project(requests.Find(id)!));
    }

    internal static AdminRequest Project(AppRequestRecord record) => new(
        record.Id,
        record.Text,
        record.DeviceName,
        record.RequestedBy,
        record.Status,
        record.Reason,
        record.DecidedBy,
        record.CreatedAt,
        record.DecidedAt);
}
