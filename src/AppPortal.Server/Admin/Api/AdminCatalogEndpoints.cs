using System.Text;

using AppPortal.Server.Action1;
using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Catalog;
using AppPortal.Shared;

namespace AppPortal.Server.Admin.Api;

/// <summary>
/// The catalog, as the catalog list and the edit form work it: read, write, hide, delete, import,
/// export, and the four helpers that fill the package fields in.
/// </summary>
public static class AdminCatalogEndpoints
{
    public static void MapAdminCatalogEndpoints(this IEndpointRouteBuilder group)
    {
        group.MapGet("/catalog", (HttpContext context, CatalogStore catalog) =>
        {
            var filter = SearchFilter.Read(context.Request.FilterValues());
            var query = context.Request.ReadSlice();
            return Results.Ok(AdminApi.Page(catalog.List(filter, query), query, Project));
        });

        group.MapGet("/catalog/{id}", (string id, CatalogStore catalog) =>
            catalog.Find(id) is { } entry ? Results.Ok(Project(entry)) : AdminApi.NotFound($"No app with id '{id}'."));

        group.MapPut("/catalog/{id}", (string id, AdminCatalogApp body, CatalogStore catalog) =>
        {
            var entry = Read((id ?? "").Trim(), body);
            try
            {
                // The store holds every rule, the reserved ids and the prerequisites included, so this
                // route refuses exactly what the form and an import refuse.
                catalog.Upsert(entry);
            }
            catch (PrerequisiteException ex)
            {
                return AdminApi.Problem(StatusCodes.Status422UnprocessableEntity, ex.Message);
            }
            catch (InvalidDataException ex)
            {
                return AdminApi.BadRequest(ex.Message);
            }

            return Results.Ok(Project(catalog.Find(entry.Id)!));
        });

        group.MapDelete("/catalog/{id}", (string id, CatalogStore catalog) =>
        {
            if (catalog.Find(id) is null)
            {
                return AdminApi.NotFound($"No app with id '{id}'.");
            }

            return catalog.Delete(id)
                ? Results.NoContent()
                : AdminApi.Conflict($"'{id}' cannot be deleted because installs refer to it. Hide it instead to take it off devices and keep the history.");
        });

        group.MapPost("/catalog/{id}/hidden", (string id, AdminCatalogHidden body, CatalogStore catalog) =>
            catalog.SetHidden(id, body.Hidden) && catalog.Find(id) is { } entry
                ? Results.Ok(Project(entry))
                : AdminApi.NotFound($"No app with id '{id}'."));

        // The page takes a file upload; here the catalog file is the body, because a JSON API client
        // has the bytes already and multipart would only be a shape to build and take apart again.
        group.MapPost("/catalog/import", async (HttpContext context, CatalogStore catalog) =>
        {
            string json;
            using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8))
            {
                var buffer = new char[AdminApiLimits.MaxImportBytes + 1];
                var read = await reader.ReadBlockAsync(buffer);
                if (read > AdminApiLimits.MaxImportBytes)
                {
                    return AdminApi.Problem(StatusCodes.Status413PayloadTooLarge,
                        $"A catalog file may be at most {AdminApiLimits.MaxImportBytes / (1024 * 1024)} MB.");
                }

                json = new string(buffer, 0, read);
            }

            if (json.Trim().Length == 0)
            {
                return AdminApi.BadRequest("Send a catalog file to import.");
            }

            try
            {
                return Results.Ok(new AdminCatalogImported(catalog.Import(CatalogStore.Parse(json))));
            }
            catch (PrerequisiteException ex)
            {
                // The same status PUT gives the same fault: the file is well formed, and what it says
                // cannot be held by the catalog it would land in.
                return AdminApi.Problem(StatusCodes.Status422UnprocessableEntity, ex.Message);
            }
            catch (InvalidDataException ex)
            {
                return AdminApi.BadRequest(ex.Message);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return AdminApi.BadRequest("That file is not valid JSON. " + ex.Message);
            }
        });

        group.MapGet("/catalog/export", (CatalogStore catalog) =>
            Results.Text(catalog.ExportJson(), "application/json"));

        group.MapPost("/catalog/action1/search", async (AdminPackageSearch body, IAction1Client action1, CancellationToken ct) =>
        {
            try
            {
                var packages = await action1.SearchPackagesAsync(body?.Term ?? "", ct);
                return Results.Ok(packages
                    .Select(p => new AdminPackageResult(p.Id, p.Name, p.Vendor, p.Builtin))
                    .ToList());
            }
            catch (Action1Exception ex)
            {
                return AdminApi.Problem(StatusCodes.Status502BadGateway, ex.Message);
            }
        });

        group.MapPost("/catalog/action1/verify", async (AdminPackageRef body, IAction1Client action1, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(body?.PackageId))
            {
                return AdminApi.BadRequest("Enter a package id first.");
            }

            try
            {
                var resolved = await action1.ResolvePackageVersionAsync(
                    body.PackageId.Trim(),
                    string.IsNullOrWhiteSpace(body.Version) ? "latest" : body.Version.Trim(),
                    ct);
                return Results.Ok(resolved is null
                    ? new AdminPackageVerified(false, "The Software Repository has no such package or version.")
                    : new AdminPackageVerified(true, $"Resolved to version {resolved.Version}."));
            }
            catch (Action1Exception ex)
            {
                return AdminApi.Problem(StatusCodes.Status502BadGateway, "Action1 could not be reached. " + ex.Message);
            }
        });

        // The two agent-package helpers on the edit form. Not in the plan's route list, which names only
        // the Action1 pair; they are here because the acceptance criterion is that every action the web
        // UI offers has an endpoint, and these two are buttons on that same form.
        group.MapPost("/catalog/package/hash", async (AdminInstallerRequest body, IConfiguration configuration, CancellationToken ct) =>
        {
            try
            {
                var maxBytes = configuration.GetValue<long?>("Catalog:MaxDownloadBytes") ?? PackageHelpers.DefaultMaxDownloadBytes;
                var result = await PackageHelpers.Shared.FetchAndHashAsync(body?.Url ?? "", maxBytes, ct);
                return Results.Ok(new AdminInstallerHash(result.Sha256, result.SizeBytes));
            }
            catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException or OperationCanceledException)
            {
                return AdminApi.BadRequest("Could not fetch the installer. " + ex.Message);
            }
        });

        group.MapPost("/catalog/package/winget", async (AdminPackageRef body, CancellationToken ct) =>
        {
            try
            {
                var lookup = await PackageHelpers.Shared
                    .LookupWingetAsync((body?.PackageId ?? "").Trim(), ct, body?.Source ?? WingetSources.Winget);
                return Results.Ok(new AdminWingetLookup(lookup.Exists, lookup.Message));
            }
            catch (InvalidDataException ex)
            {
                return AdminApi.BadRequest(ex.Message);
            }
        });
    }

    /// <summary>The store's entry as the wire sees it. Every field the edit form can change is here.</summary>
    internal static AdminCatalogApp Project(CatalogEntry entry) => new(
        entry.Id,
        entry.Name,
        entry.Publisher,
        entry.Description,
        entry.Category,
        entry.IconUrl,
        entry.Featured,
        entry.Hidden,
        entry.EngineOverride,
        entry.Requirements,
        [.. entry.Requires],
        entry.UserRemovable,
        entry.Match is null ? null : new AdminMatchRule(entry.Match.NameContains, entry.Match.NameEquals),
        new AdminAction1Package(entry.Action1.PackageId, entry.Action1.Version),
        entry.Agent);

    /// <summary>
    /// The wire shape back into a store entry, with the same tidying the form does: a blank is nothing,
    /// a missing category is Other, and a missing version is latest. The store validates the rest.
    /// </summary>
    private static CatalogEntry Read(string id, AdminCatalogApp body) => new()
    {
        Id = id,
        Name = (body.Name ?? "").Trim(),
        Publisher = body.Publisher ?? "",
        Description = body.Description ?? "",
        Category = string.IsNullOrWhiteSpace(body.Category) ? "Other" : body.Category,
        IconUrl = Empty(body.IconUrl),
        Featured = body.Featured,
        Hidden = body.Hidden,
        EngineOverride = Empty(body.EngineOverride),
        Requirements = Empty(body.Requirements),
        Requires = [.. (body.Requires ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim())],
        UserRemovable = body.UserRemovable,
        Match = body.Match is null || (Empty(body.Match.NameContains) is null && Empty(body.Match.NameEquals) is null)
            ? null
            : new MatchRule { NameContains = Empty(body.Match.NameContains), NameEquals = Empty(body.Match.NameEquals) },
        Action1 = new Action1PackageRef
        {
            PackageId = (body.Action1?.PackageId ?? "").Trim(),
            Version = string.IsNullOrWhiteSpace(body.Action1?.Version) ? "latest" : body.Action1.Version.Trim(),
        },
        Agent = body.Agent,
    };

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
