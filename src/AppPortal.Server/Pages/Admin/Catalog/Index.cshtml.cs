using AppPortal.Server.Admin;
using AppPortal.Server.Catalog;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin.Catalog;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(CatalogStore catalog) : PageModel
{
    public IReadOnlyList<CatalogEntry> Apps { get; private set; } = [];

    public string? Error { get; private set; }

    public string? Notice { get; private set; }

    [BindProperty(SupportsGet = true)]
    public string Search { get; set; } = "";

    public void OnGet() => Load();

    public IActionResult OnPostHide(string id, bool hidden)
    {
        Notice = catalog.SetHidden(id ?? "", hidden)
            ? hidden
                ? $"'{id}' is hidden. Devices are no longer offered it."
                : $"'{id}' is visible to devices again."
            : null;
        if (Notice is null)
        {
            Error = $"No app with id '{id}'.";
        }

        return Done();
    }

    public IActionResult OnPostDelete(string id)
    {
        if (catalog.Delete(id ?? ""))
        {
            Notice = $"'{id}' is deleted.";
        }
        else
        {
            Error = $"'{id}' cannot be deleted because installs refer to it. Hide it instead to take it off devices and keep the history.";
        }

        return Done();
    }

    public IActionResult OnPostImport(IFormFile? file)
    {
        if (file is null || file.Length == 0)
        {
            Error = "Choose a catalog file to import.";
            return Done();
        }

        try
        {
            using var reader = new StreamReader(file.OpenReadStream());
            var count = catalog.Import(CatalogStore.Parse(reader.ReadToEnd()));
            Notice = $"Imported {count} app{(count == 1 ? "" : "s")}.";
        }
        catch (InvalidDataException ex)
        {
            Error = ex.Message;
        }
        catch (System.Text.Json.JsonException ex)
        {
            Error = "That file is not valid JSON. " + ex.Message;
        }

        return Done();
    }

    public IActionResult OnGetExport()
        => File(System.Text.Encoding.UTF8.GetBytes(catalog.ExportJson()), "application/json", "catalog.json");

    public CatalogTableView Table => new(Apps, Error, Notice);

    private void Load() => Apps = catalog.Search(Search);

    /// <summary>htmx asked for the table alone, so give it the table alone; a plain browser post gets the page.</summary>
    private IActionResult Done()
    {
        Load();
        return Request.Headers.ContainsKey("HX-Request")
            ? Partial("_CatalogTable", Table)
            : Page();
    }
}
