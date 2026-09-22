using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Enrollment;
using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>Enrollment keys, as the keys list and one key's audit page work them.</summary>
public static class AdminKeyEndpoints
{
    public static void MapAdminKeyEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/keys", (HttpContext context, EnrollmentKeyStore keys) =>
        {
            var query = context.Request.ReadSlice();
            return Results.Ok(AdminApi.Page(keys.List(NoFilter.Instance, query), query, Project));
        });

        group.MapPost("/keys", (EnrollmentKeyCreate body, HttpContext context, EnrollmentKeyStore keys) =>
        {
            try
            {
                var created = keys.Create(
                    body?.Name ?? "",
                    EnrollmentKeyStore.ParseEngine(body?.Engine),
                    body?.ExpiresAt,
                    body?.MaxUses,
                    context.Username());

                // The plaintext is in this reply and nowhere else. It is not stored, and the request log
                // records the route and the administrator only, so it cannot reach a log from here.
                return Results.Created(
                    $"{AdminApiRoutes.Keys}/{created.Key.Id}",
                    new AppPortal.Shared.EnrollmentKeyCreated(Project(created.Key), created.Plaintext));
            }
            catch (EnrollmentKeyRejectedException ex)
            {
                return AdminApi.BadRequest(ex.Message);
            }
        });

        // Not in the plan's route list, which has the list, the create, the revoke and the events. The
        // key detail page shows one key beside its attempts, and finding that row by paging the whole
        // list is not the same thing, so the read is here.
        group.MapGet("/keys/{id}", (string id, EnrollmentKeyStore keys) =>
            keys.Find(id) is { } key ? Results.Ok(Project(key)) : AdminApi.NotFound("No such enrollment key."));

        group.MapPost("/keys/{id}/revoke", (string id, EnrollmentKeyStore keys) =>
        {
            if (keys.Find(id) is null)
            {
                return AdminApi.NotFound("No such enrollment key.");
            }

            // Revoking an already revoked key is not an error: the caller wanted it revoked and it is.
            keys.Revoke(id);
            return Results.Ok(Project(keys.Find(id)!));
        });

        group.MapGet("/keys/{id}/events", (string id, HttpContext context, EnrollmentKeyStore keys, EnrollmentEventStore events) =>
        {
            if (keys.Find(id) is null)
            {
                return AdminApi.NotFound("No such enrollment key.");
            }

            // The newest attempts and no paging, the same as the key detail page shows them. The attempts
            // that matter are the recent ones, and a key's history is read to see why a rollout is failing
            // now, not to walk it to the start. An offset is refused rather than ignored: a script paging
            // through would be handed the first slice again on every call and never reach the end.
            var query = context.Request.ReadSlice();
            if (query.Offset > 0)
            {
                return AdminApi.BadRequest("This list is the newest attempts only and does not page. Leave out offset and raise limit instead.");
            }

            return Results.Ok(events.ForKey(id, query.Limit ?? EnrollmentEventStore.PageSize)
                .Select(e => new EnrollmentKeyEvent(e.Id, e.DeviceId, e.DeviceName, e.Source,
                    EnrollmentEventStore.Name(e.Outcome), e.Describe, e.CreatedAt))
                .ToList());
        });
    }

    private static EnrollmentKeySummary Project(EnrollmentKeyRecord key) => new(
        key.Id,
        key.Name,
        key.KeyPrefix,
        EnrollmentKeyStore.Name(key.DefaultEngine),
        key.Status.ToString().ToLowerInvariant(),
        key.ExpiresAt,
        key.MaxUses,
        key.Uses,
        key.RevokedAt,
        key.CreatedBy,
        key.CreatedAt);
}
