using AppPortal.Server.Admin;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace AppPortal.Server.Pages.Admin.Installs;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(InstallStore installs, DeviceStore devices, CatalogStore catalog) : PageModel
{
    /// <summary>One screenful. The filters are the way to narrow a fleet, not a deeper page count.</summary>
    public const int PageSize = 100;

    public IReadOnlyList<InstallRecord> Installs { get; private set; } = [];

    public int Total { get; private set; }

    public IReadOnlyList<string> DeviceNames { get; private set; } = [];

    public IReadOnlyList<CatalogEntry> Apps { get; private set; } = [];

    [BindProperty(SupportsGet = true)]
    public string Device { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string App { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string State { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string Requester { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string From { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public string To { get; set; } = "";

    [BindProperty(SupportsGet = true)]
    public int Skip { get; set; }

    public InstallState[] States { get; } = Enum.GetValues<InstallState>();

    public bool HasPrevious => Skip > 0;

    public bool HasNext => Skip + PageSize < Total;

    public int FirstShown => Total == 0 ? 0 : Skip + 1;

    public int LastShown => Math.Min(Skip + PageSize, Total);

    /// <summary>True while anything on this page could still move, which is when polling earns its keep.</summary>
    public bool HasActive => Installs.Any(i => i.IsActive);

    public void OnGet() => Load();

    /// <summary>
    /// htmx polls this every 30 seconds while a row is active and swaps the table. The filters travel
    /// with it, so a poll shows the same slice the administrator is looking at.
    /// </summary>
    public IActionResult OnGetRows()
    {
        Load();
        return Partial("_InstallRows", this);
    }

    public string QueryFor(int skip)
    {
        var parts = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"{key}={Uri.EscapeDataString(value)}");
            }
        }

        Add("Device", Device);
        Add("App", App);
        Add("State", State);
        Add("Requester", Requester);
        Add("From", From);
        Add("To", To);
        if (skip > 0)
        {
            parts.Add($"Skip={skip}");
        }

        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }

    private void Load()
    {
        var filter = new InstallFilter(
            Device,
            App,
            Enum.TryParse<InstallState>(State, ignoreCase: true, out var state) ? state : null,
            Requester,
            ParseDate(From),
            // A date typed in the To box means the whole of that day, so the range runs to the next midnight.
            ParseDate(To)?.AddDays(1));

        Skip = Math.Max(0, Skip);
        Total = installs.CountMatching(filter);
        Installs = installs.ListRecent(filter, PageSize, Skip);
        DeviceNames = [.. devices.All().Select(d => d.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        Apps = catalog.Entries;
    }

    /// <summary>
    /// A date out of the picker has no offset, and it means a day where the administrator is, which is
    /// the same clock the table's times are rendered on. Assuming UTC would slide the boundary by the
    /// server's offset and quietly drop or add a few hours' worth of rows at each end.
    /// </summary>
    private static DateTimeOffset? ParseDate(string? value)
        => DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal,
            out var parsed)
            ? parsed
            : null;
}
