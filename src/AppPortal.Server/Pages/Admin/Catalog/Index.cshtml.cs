using AppPortal.Server.Admin;
using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Catalog;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppPortal.Server.Pages.Admin.Catalog;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(CatalogStore catalog) : AdminListPage<SearchFilter, CatalogEntry>
{
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

    protected override string TablePartial => "_CatalogTable";

    protected override void Load() => Slice = catalog.List(Filter, Query);
}
