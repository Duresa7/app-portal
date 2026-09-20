using AppPortal.Server.Admin;
using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppPortal.Server.Pages.Admin.Installs;

[Authorize(Policy = AdminAuth.Policy)]
public sealed class IndexModel(InstallStore installs, DeviceStore devices, CatalogStore catalog) : AdminListPage<InstallFilter, InstallRecord>
{
    /// <summary>One screenful. The filters are the way to narrow a fleet, not a deeper page count.</summary>
    public const int PageSize = 100;

    /// <summary>Counted in rows, and with the total, because the pager says "Showing 1 to 100 of 340".</summary>
    private static readonly Paging Pages = Paging.ByRows("Skip", PageSize, wantTotal: true);

    public IReadOnlyList<string> DeviceNames { get; private set; } = [];

    public IReadOnlyList<CatalogEntry> Apps { get; private set; } = [];

    public InstallState[] States { get; } = Enum.GetValues<InstallState>();

    public override Paging Paging => Pages;

    protected override string TablePartial => "_InstallRows";

    /// <summary>
    /// htmx polls this every 30 seconds and swaps the table. The filters travel
    /// with it, so a poll shows the same slice the administrator is looking at.
    /// </summary>
    public IActionResult OnGetRows()
    {
        Load();
        return Table();
    }

    protected override void Load()
    {
        Slice = installs.List(Filter, Query);
        DeviceNames = [.. devices.All().Select(d => d.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
        Apps = catalog.Entries;
    }
}
