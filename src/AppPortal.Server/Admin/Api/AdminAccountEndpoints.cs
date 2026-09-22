using AppPortal.Server.Admin.Lists;
using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>Administrator accounts, as the administrators page works them.</summary>
public static class AdminAccountEndpoints
{
    public static void MapAdminAccountEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/admins", (HttpContext context, AdminStore admins) =>
        {
            var query = context.Request.ReadSlice();
            return Results.Ok(AdminApi.Page(admins.List(NoFilter.Instance, query), query, Project));
        });

        group.MapPost("/admins", (AdminAccountCreate body, AdminStore admins) =>
        {
            try
            {
                var record = admins.Add(body?.Username ?? "", body?.Password ?? "");
                // No Location header: there is no route that reads one administrator, and a header that
                // names a path answering 404 is worse than none. The body is the account as created.
                return Results.Created((string?)null, Project(record));
            }
            catch (AdminRejectedException ex)
            {
                return AdminApi.BadRequest(ex.Message);
            }
        });

        group.MapPost("/admins/{id}/disable", (string id, HttpContext context, AdminStore admins, AdminSessionStore sessions) =>
        {
            if (admins.FindById(id) is not { } record)
            {
                return AdminApi.NotFound("No such administrator.");
            }

            if (record.Id == context.AdminId())
            {
                // Signing yourself out by disabling yourself is a support call waiting to happen.
                return AdminApi.Conflict("You cannot disable the account you are signed in with.");
            }

            try
            {
                admins.SetDisabled(record.Username, true);
            }
            catch (AdminRejectedException ex)
            {
                return AdminApi.Conflict(ex.Message);
            }

            // Disabling has to bite now, not at expiry: every session that account holds goes with it.
            sessions.RevokeAllFor(record.Id);
            return Results.Ok(Project(admins.FindById(id)!));
        });

        group.MapPost("/admins/{id}/reset-password", (string id, AdminPasswordReset body, AdminStore admins, AdminSessionStore sessions) =>
        {
            if (admins.FindById(id) is not { } record)
            {
                return AdminApi.NotFound("No such administrator.");
            }

            try
            {
                admins.SetPassword(record.Username, body?.Password ?? "");
            }
            catch (AdminRejectedException ex)
            {
                return AdminApi.BadRequest(ex.Message);
            }

            // A changed password signs that account out everywhere, the same as the page does it.
            sessions.RevokeAllFor(record.Id);
            return Results.NoContent();
        });
    }

    private static AdminAccount Project(AdminRecord record)
        => new(record.Id, record.Username, record.Disabled, record.Source, record.CreatedAt, record.LastLoginAt);
}
