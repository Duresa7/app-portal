using AppPortal.Server.Settings;
using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>
/// The settings the whole server shares. Named <c>AdminSettings</c> on the wire because
/// <see cref="PortalSettings"/> is already the client's own configuration file on a PC.
/// </summary>
public static class AdminSettingsEndpoints
{
    public static void MapAdminSettingsEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/settings", (SettingsStore settings) => Results.Ok(new AdminSettings(settings.DefaultEngine)));

        group.MapPut("/settings", (AdminSettings body, SettingsStore settings) =>
        {
            var chosen = (body?.DefaultEngine ?? "").Trim().ToLowerInvariant();
            if (chosen is not (EngineLabel.Action1 or EngineLabel.Agent))
            {
                // Storing anything else would leave a setting the selector quietly ignores, which is
                // worse than refusing it: the caller would read back a value that does nothing.
                return AdminApi.BadRequest("Choose either Action1 or Agent.");
            }

            settings.Set(SettingsStore.DefaultEngineKey, chosen);
            return Results.Ok(new AdminSettings(chosen));
        });
    }
}
