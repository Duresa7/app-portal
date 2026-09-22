using System.ComponentModel.DataAnnotations;

using AppPortal.Server.Action1;
using AppPortal.Server.Admin;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
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
public sealed class EditModel(CatalogStore catalog, IAction1Client action1, IConfiguration configuration,
    DeviceManagerStore managers, PackageHelpers? helpers = null) : PageModel
{
    public const string NewId = "new";

    private IReadOnlyDictionary<string, int>? _devicesByManager;

    /// <summary>How many enrolled devices report this package manager, read once per request.</summary>
    public int DevicesWith(string manager)
        => (_devicesByManager ??= managers.DevicesByManager()).GetValueOrDefault(manager);

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

    /// <summary>What saving the form will do, in words. Worked out from the same entry a save writes.</summary>
    public string Sentence { get; private set; } = "";

    public const string SourceAction1 = "action1";
    public const string SourceDirect = "direct";

    /// <summary>
    /// Where the app comes from, the first thing an administrator chooses: <c>action1</c>,
    /// <c>winget</c>, <c>msstore</c>, <c>direct</c>, or the name of a package manager. No manager is
    /// called any of the other four, so one value is enough.
    /// </summary>
    [BindProperty]
    public string Source { get; set; } = SourceAction1;

    /// <summary>The package's id in its source, for winget, the Store and every manager.</summary>
    [BindProperty]
    public string SourceId { get; set; } = "";

    [BindProperty]
    public string SourceVersion { get; set; } = "";

    [BindProperty]
    public string SourceExtraArgs { get; set; } = "";

    /// <summary>Machine or user, for every agent source, the direct download too.</summary>
    [BindProperty]
    public string SourceScope { get; set; } = "machine";

    [BindProperty]
    public bool SourceRequiresReboot { get; set; }

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

    public IActionResult OnGet(string id)
    {
        IsNew = string.Equals(id, NewId, StringComparison.OrdinalIgnoreCase);
        if (IsNew)
        {
            Category = "Other";
            Sentence = new CatalogEntry().Describe();
            return Page();
        }

        var entry = catalog.Find(id);
        if (entry is null)
        {
            return NotFound();
        }

        Fill(entry);
        Sentence = entry.Describe();
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

        if (ModelState[nameof(DirectSizeBytes)]?.Errors.Count > 0 && Source == SourceDirect)
        {
            Error = "Enter sizeBytes as a positive whole number of bytes.";
            return Page();
        }

        CatalogEntry entry;
        try
        {
            entry = Build(target);
            // Before the save, not after: a loop written into the catalog is a loop every install of
            // those apps has to walk around, and the person who can undo it is the one on this page.
            catalog.EnsureNoCycle(entry.Id, entry.Requires);
            catalog.Upsert(entry);
        }
        catch (Exception ex) when (ex is PrerequisiteException or InvalidDataException)
        {
            Error = ex.Message;
            Sentence = TryDescribe(target);
            return Page();
        }

        return RedirectToPage("Edit", new { id = entry.Id, saved = true });
    }

    /// <summary>
    /// htmx: the sentence under the form, redone whenever a field changes. It describes the entry a
    /// save would write, so it cannot promise something the save would not do.
    /// </summary>
    public IActionResult OnPostDescribe([FromRoute] string id)
    {
        IsNew = string.Equals(id, NewId, StringComparison.OrdinalIgnoreCase);
        return Content(System.Net.WebUtility.HtmlEncode(TryDescribe(IsNew ? Id ?? "" : id)), "text/html");
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
                .LookupWingetAsync((SourceId ?? "").Trim(), ct,
                    Source == WingetSources.Store ? WingetSources.Store : WingetSources.Winget)).Message;
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

    private string TryDescribe(string target)
    {
        try
        {
            return Build(target).Describe();
        }
        catch (InvalidDataException ex)
        {
            return "Not ready to save: " + ex.Message;
        }
    }

    /// <summary>
    /// The entry this form describes, for a save and for the sentence alike. Throws
    /// <see cref="InvalidDataException"/> with the reason it cannot be saved.
    /// </summary>
    private CatalogEntry Build(string target)
    {
        var entry = new CatalogEntry
        {
            Id = target.Trim(),
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

        var id = (SourceId ?? "").Trim();
        entry.Agent = Source switch
        {
            null or "" or SourceAction1 => null,
            WingetSources.Winget or WingetSources.Store => new WingetPackageDefinition(id, SourceScope,
                EmptyToNull(SourceVersion), EmptyToNull(SourceExtraArgs), SourceRequiresReboot, Source),
            SourceDirect => new DirectPackageDefinition((DirectUrl ?? "").Trim(), (DirectSha256 ?? "").Trim(),
                DirectInstallerType, DirectSilentArgs ?? "", DirectSizeBytes ?? 0, EmptyToNull(DirectUninstallKey),
                SourceScope, SourceRequiresReboot),
            _ when PackageManagers.Find(Source) is not null => new ManagedPackageDefinition(Source, id, SourceScope,
                EmptyToNull(SourceVersion), EmptyToNull(SourceExtraArgs), SourceRequiresReboot),
            _ => throw new InvalidDataException("Choose where this app comes from: Action1, winget, the Microsoft Store, "
                                                + "a package manager, or a direct download."),
        };
        return entry;
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
        EngineOverride = entry.EngineOverride ?? "";
        Requirements = entry.Requirements ?? "";
        Requires = string.Join('\n', entry.Requires);
        UserRemovable = entry.UserRemovable;
        MatchNameContains = entry.Match?.NameContains ?? "";
        MatchNameEquals = entry.Match?.NameEquals ?? "";
        PackageId = entry.Action1.PackageId;
        Version = entry.Action1.Version;
        Source = SourceAction1;
        if (entry.Agent is { } agent)
        {
            SourceScope = agent.Scope;
            SourceRequiresReboot = agent.RequiresReboot;
        }

        switch (entry.Agent)
        {
            case WingetPackageDefinition winget:
                Source = winget.Source;
                SourceId = winget.Id;
                SourceVersion = winget.Version ?? "";
                SourceExtraArgs = winget.ExtraArgs ?? "";
                break;
            case DirectPackageDefinition direct:
                Source = SourceDirect;
                DirectUrl = direct.Url;
                DirectSha256 = direct.Sha256;
                DirectInstallerType = direct.InstallerType;
                DirectSilentArgs = direct.SilentArgs;
                DirectSizeBytes = direct.SizeBytes;
                DirectUninstallKey = direct.UninstallKey ?? "";
                break;
            case ManagedPackageDefinition managed:
                Source = managed.Manager;
                SourceId = managed.Id;
                SourceVersion = managed.Version ?? "";
                SourceExtraArgs = managed.ExtraArgs ?? "";
                break;
        }
    }
}

public sealed record PackageSearchView(IReadOnlyList<Action1Package> Packages, string? Error);

public sealed record PackageVerifyView(bool Ok, string Message);
