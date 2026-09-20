using AppPortal.Server.Action1;
using AppPortal.Server.Admin;
using AppPortal.Server.Catalog;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin.Catalog;

/// <summary>
/// One catalog app. The id <c>new</c> is the create form, which is why an app may not be called that;
/// every other id edits the app it names.
/// </summary>
[Authorize(Policy = AdminAuth.Policy)]
public sealed class EditModel(CatalogStore catalog, IAction1Client action1) : PageModel
{
    public const string NewId = "new";

    public bool IsNew { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Set by the redirect after a save, so a reload does not repost the form.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Saved { get; set; }

    [BindProperty]
    public string Id { get; set; } = "";

    [BindProperty]
    public string Name { get; set; } = "";

    [BindProperty]
    public string Publisher { get; set; } = "";

    [BindProperty]
    public string Description { get; set; } = "";

    [BindProperty]
    public string Category { get; set; } = "";

    [BindProperty]
    public string IconUrl { get; set; } = "";

    [BindProperty]
    public bool Featured { get; set; }

    [BindProperty]
    public bool Hidden { get; set; }

    [BindProperty]
    public string MatchNameContains { get; set; } = "";

    [BindProperty]
    public string MatchNameEquals { get; set; } = "";

    [BindProperty]
    public string PackageId { get; set; } = "";

    [BindProperty]
    public string Version { get; set; } = "latest";

    public IActionResult OnGet(string id)
    {
        IsNew = string.Equals(id, NewId, StringComparison.OrdinalIgnoreCase);
        if (IsNew)
        {
            Category = "Other";
            return Page();
        }

        var entry = catalog.Find(id);
        if (entry is null)
        {
            return NotFound();
        }

        Fill(entry);
        return Page();
    }

    public IActionResult OnPost([FromRoute] string id)
    {
        IsNew = string.Equals(id, NewId, StringComparison.OrdinalIgnoreCase);
        var target = ((IsNew ? Id : id) ?? "").Trim();

        if (IsNew && string.Equals(target.Trim(), NewId, StringComparison.OrdinalIgnoreCase))
        {
            Error = $"'{NewId}' is reserved for the create form. Give the app another id.";
            return Page();
        }

        if (IsNew && catalog.Find(target) is not null)
        {
            Error = $"An app with id '{target.Trim()}' already exists.";
            return Page();
        }

        var entry = new CatalogEntry
        {
            Id = target,
            Name = Name ?? "",
            Publisher = Publisher ?? "",
            Description = Description ?? "",
            Category = string.IsNullOrWhiteSpace(Category) ? "Other" : Category,
            IconUrl = string.IsNullOrWhiteSpace(IconUrl) ? null : IconUrl.Trim(),
            Featured = Featured,
            Hidden = Hidden,
            Match = string.IsNullOrWhiteSpace(MatchNameContains) && string.IsNullOrWhiteSpace(MatchNameEquals)
                ? null
                : new MatchRule
                {
                    NameContains = string.IsNullOrWhiteSpace(MatchNameContains) ? null : MatchNameContains.Trim(),
                    NameEquals = string.IsNullOrWhiteSpace(MatchNameEquals) ? null : MatchNameEquals.Trim(),
                },
            Action1 = new Action1PackageRef
            {
                PackageId = (PackageId ?? "").Trim(),
                Version = string.IsNullOrWhiteSpace(Version) ? "latest" : Version.Trim(),
            },
        };

        try
        {
            catalog.Upsert(entry);
        }
        catch (InvalidDataException ex)
        {
            Error = ex.Message;
            return Page();
        }

        return RedirectToPage("Edit", new { id = entry.Id.Trim(), saved = true });
    }

    /// <summary>htmx: the Search button under the Action1 package section.</summary>
    public async Task<IActionResult> OnGetSearchAsync(string? term, CancellationToken ct)
    {
        try
        {
            var packages = await action1.SearchPackagesAsync(term ?? "", ct);
            return Partial("_PackageSearch", new PackageSearchView(packages, null));
        }
        catch (Action1Exception ex)
        {
            return Partial("_PackageSearch", new PackageSearchView([], ex.Message));
        }
    }

    /// <summary>htmx: the Verify button, which resolves the package and version against Action1.</summary>
    public async Task<IActionResult> OnGetVerifyAsync(string? packageId, string? version, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            return Partial("_PackageVerify", new PackageVerifyView(false, "Enter a package id first."));
        }

        try
        {
            var resolved = await action1.ResolvePackageVersionAsync(
                packageId.Trim(),
                string.IsNullOrWhiteSpace(version) ? "latest" : version.Trim(),
                ct);
            return Partial("_PackageVerify", resolved is null
                ? new PackageVerifyView(false, "The Software Repository has no such package or version.")
                : new PackageVerifyView(true, $"Resolved to version {resolved.Version}."));
        }
        catch (Action1Exception ex)
        {
            return Partial("_PackageVerify", new PackageVerifyView(false, "Action1 could not be reached. " + ex.Message));
        }
    }

    private void Fill(CatalogEntry entry)
    {
        Id = entry.Id;
        Name = entry.Name;
        Publisher = entry.Publisher;
        Description = entry.Description;
        Category = entry.Category;
        IconUrl = entry.IconUrl ?? "";
        Featured = entry.Featured;
        Hidden = entry.Hidden;
        MatchNameContains = entry.Match?.NameContains ?? "";
        MatchNameEquals = entry.Match?.NameEquals ?? "";
        PackageId = entry.Action1.PackageId;
        Version = entry.Action1.Version;
    }
}

public sealed record PackageSearchView(IReadOnlyList<Action1Package> Packages, string? Error);

public sealed record PackageVerifyView(bool Ok, string Message);
