using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>
/// The sessions an administrator holds. The web admin UI has no such page, so this adds no power over
/// the fleet: it only lets somebody see which machines are signed in as them and cut one off. That is
/// the other half of a token that now lives for thirty days.
/// </summary>
public static class AdminSessionEndpoints
{
    public static void MapAdminSessionEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/sessions", (HttpContext context, AdminSessionStore sessions) =>
        {
            var current = sessions.IdOf(context.SessionToken());
            return Results.Ok(sessions.ListFor(context.AdminId())
                .Select(session => Summary(session, current))
                .ToList());
        });

        // Only the caller's own sessions, and 404 rather than 403 for anything else: an administrator
        // must not be able to find out whether somebody else's session id exists by asking to delete it.
        group.MapDelete("/sessions/{id}", (string id, HttpContext context, AdminSessionStore sessions) =>
            sessions.RevokeById(context.AdminId(), id)
                ? Results.NoContent()
                : AdminApi.NotFound("You have no session with that id."));
    }

    private static AdminSessionSummary Summary(AdminSessionRecord session, string? currentId)
        => new(
            session.Id,
            session.DeviceName,
            session.Kind == AdminSessionKind.Web ? "web" : "api",
            session.CreatedAt,
            session.ExpiresAt,
            session.LastUsedAt,
            currentId is not null && session.Id == currentId);
}
