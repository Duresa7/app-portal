using System.ComponentModel.DataAnnotations;

using AppPortal.Server.Action1;
using AppPortal.Server.Admin;
using AppPortal.Server.Catalog;
using AppPortal.Shared;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin.Catalog;

/// <summary>
/// One catalog app. The id <c>new</c> is the create form, which is why an app may not be called that;
/// every other id edits the app it names.
/// </summary>
[Authorize(Policy = AdminAuth.Policy)]
public sealed class EditModel(CatalogStore catalog, IAction1Client action1, IConfiguration configuration, PackageHelpers? helpers = null) : PageModel
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

    /// <summary>Empty to follow the server's default, which is what almost every app should do.</summary>
    [BindProperty]
    public string EngineOverride { get; set; } = "";

    [BindProperty]
    [StringLength(AppPortal.Shared.CatalogLimits.MaxRequirementsLength,
        ErrorMessage = "Requirements must be 500 characters or fewer.")]
    public string Requirements { get; set; } = "";

    /// <summary>App ids this one needs first, one per line, in the order they should be installed.</summary>
    [BindProperty]
    public string Requires { get; set; } = "";

    [BindProperty]
    public bool UserRemovable { get; set; }


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

    public string? HelperMessage { get; private set; }

    [BindProperty]
    public string AgentKind { get; set; } = "";

    [BindProperty]
    public string WingetSource { get; set; } = WingetSources.Winget;

    [BindProperty]
    public string WingetId { get; set; } = "";

    [BindProperty]
    public string WingetScope { get; set; } = "machine";

    [BindProperty]
    public string WingetVersion { get; set; } = "";

    [BindProperty]
    public string WingetExtraArgs { get; set; } = "";

    [BindProperty]
    public bool WingetRequiresReboot { get; set; }

    [BindProperty]
    public string DirectUrl { get; set; } = "";

    [BindProperty]
    public string DirectSha256 { get; set; } = "";

    [BindProperty]
    public string DirectInstallerType { get; set; } = "exe";

    [BindProperty]
    public string DirectSilentArgs { get; set; } = "";

    [BindProperty]
    public long? DirectSizeBytes { get; set; } = null;

    [BindProperty]
    public string DirectUninstallKey { get; set; } = "";

    [BindProperty]
    public string DirectScope { get; set; } = "machine";

    [BindProperty]
    public bool DirectRequiresReboot { get; set; }

    [BindProperty]
    public string ManagedManager { get; set; } = "choco";

    [BindProperty]
    public string ManagedId { get; set; } = "";

    [BindProperty]
    public string ManagedScope { get; set; } = "machine";

    [BindProperty]
    public string ManagedVersion { get; set; } = "";

    [BindProperty]
    public string ManagedExtraArgs { get; set; } = "";

    [BindProperty]
    public bool ManagedRequiresReboot { get; set; }

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

        if (ModelState[nameof(DirectSizeBytes)]?.Errors.Count > 0 && AgentKind == "direct")
        {
            Error = "Enter sizeBytes as a positive whole number of bytes.";
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
            EngineOverride = EmptyToNull(EngineOverride),
            Requirements = EmptyToNull(Requirements),
            Requires = [.. SplitLines(Requires)],
            UserRemovable = UserRemovable,
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
            entry.Agent = AgentKind switch
            {
                null or "" => null,
                "winget" => new WingetPackageDefinition((WingetId ?? "").Trim(), WingetScope,
                    EmptyToNull(WingetVersion), EmptyToNull(WingetExtraArgs), WingetRequiresReboot,
                    WingetSource),
                "direct" => new DirectPackageDefinition((DirectUrl ?? "").Trim(), (DirectSha256 ?? "").Trim(),
                    DirectInstallerType, DirectSilentArgs, DirectSizeBytes ?? 0, EmptyToNull(DirectUninstallKey),
                    DirectScope, DirectRequiresReboot),
                "managed" => new ManagedPackageDefinition((ManagedManager ?? "").Trim(), (ManagedId ?? "").Trim(),
                    ManagedScope, EmptyToNull(ManagedVersion), EmptyToNull(ManagedExtraArgs), ManagedRequiresReboot),
                _ => throw new InvalidDataException($"The agent package kind must be {PackageDefinition.Kinds}."),
            };
            // Before the save, not after: a loop written into the catalog is a loop every install of
            // those apps has to walk around, and the person who can undo it is the one on this page.
            catalog.EnsureNoCycle(entry.Id, entry.Requires);
            catalog.Upsert(entry);
        }
        catch (PrerequisiteException ex)
        {
            Error = ex.Message;
            Fill(entry);
            return Page();
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

    public async Task<IActionResult> OnPostFetchAndHashAsync([FromRoute] string id, CancellationToken ct)
    {
        IsNew = string.Equals(id, NewId, StringComparison.OrdinalIgnoreCase);
        if (!IsNew)
        {
            Id = id;
        }

        try
        {
            var maxBytes = configuration.GetValue<long?>("Catalog:MaxDownloadBytes") ?? PackageHelpers.DefaultMaxDownloadBytes;
            var result = await (helpers ?? PackageHelpers.Shared).FetchAndHashAsync(DirectUrl, maxBytes, ct);
            DirectSha256 = result.Sha256;
            DirectSizeBytes = result.SizeBytes;
            HelperMessage = "Hash and size filled. Save to keep the definition.";
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or IOException or OperationCanceledException)
        {
            Error = "Could not fetch the installer. " + ex.Message;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostLookupWingetAsync([FromRoute] string id, CancellationToken ct)
    {
        IsNew = string.Equals(id, NewId, StringComparison.OrdinalIgnoreCase);
        if (!IsNew)
        {
            Id = id;
        }

        try
        {
            HelperMessage = (await (helpers ?? PackageHelpers.Shared)
                .LookupWingetAsync((WingetId ?? "").Trim(), ct, WingetSource)).Message;
        }
        catch (InvalidDataException ex)
        {
            Error = ex.Message;
        }

        return Page();
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>One app id per line, blank lines ignored, so the box can be typed in comfortably.</summary>
    private static IEnumerable<string> SplitLines(string? value)
        => (value ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
        EngineOverride = entry.EngineOverride ?? "";
        Requirements = entry.Requirements ?? "";
        Requires = string.Join('\n', entry.Requires);
        UserRemovable = entry.UserRemovable;
        MatchNameContains = entry.Match?.NameContains ?? "";
        MatchNameEquals = entry.Match?.NameEquals ?? "";
        PackageId = entry.Action1.PackageId;
        Version = entry.Action1.Version;
        switch (entry.Agent)
        {
            case WingetPackageDefinition winget:
                AgentKind = "winget";
                WingetSource = winget.Source;
                WingetId = winget.Id;
                WingetScope = winget.Scope;
                WingetVersion = winget.Version ?? "";
                WingetExtraArgs = winget.ExtraArgs ?? "";
                WingetRequiresReboot = winget.RequiresReboot;
                break;
            case DirectPackageDefinition direct:
                AgentKind = "direct";
                DirectUrl = direct.Url;
                DirectSha256 = direct.Sha256;
                DirectInstallerType = direct.InstallerType;
                DirectSilentArgs = direct.SilentArgs;
                DirectSizeBytes = direct.SizeBytes;
                DirectUninstallKey = direct.UninstallKey ?? "";
                DirectScope = direct.Scope;
                DirectRequiresReboot = direct.RequiresReboot;
                break;
            case ManagedPackageDefinition managed:
                AgentKind = "managed";
                ManagedManager = managed.Manager;
                ManagedId = managed.Id;
                ManagedScope = managed.Scope;
                ManagedVersion = managed.Version ?? "";
                ManagedExtraArgs = managed.ExtraArgs ?? "";
                ManagedRequiresReboot = managed.RequiresReboot;
                break;
        }
    }
}

public sealed record PackageSearchView(IReadOnlyList<Action1Package> Packages, string? Error);

public sealed record PackageVerifyView(bool Ok, string Message);
