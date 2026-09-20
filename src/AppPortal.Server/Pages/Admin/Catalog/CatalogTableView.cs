using AppPortal.Server.Catalog;

namespace AppPortal.Server.Pages.Admin.Catalog;

/// <summary>
/// What the catalog table partial renders. The messages travel with the rows because htmx swaps this
/// fragment alone: a refused delete has to explain itself inside the thing that came back, or the
/// reason never reaches the screen.
/// </summary>
public sealed record CatalogTableView(IReadOnlyList<CatalogEntry> Apps, string? Error = null, string? Notice = null);
