using AppPortal.Server.Admin.Lists;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace AppPortal.Server.Pages.Admin;

/// <summary>
/// What a table partial can see of the page that rendered it, whichever list it is: the messages an
/// edit left behind and the way to link to another place in the list. Partials receive a
/// <see cref="Slice{T}"/> as their model and reach the page through <see cref="AdminListViewData"/>.
/// </summary>
public abstract class AdminListPage : PageModel
{
    public string? Error { get; protected set; }

    public string? Notice { get; protected set; }

    /// <summary>How this page names its place in the list on the URL, or null for a list nobody pages.</summary>
    public abstract Paging? Paging { get; }

    /// <summary>
    /// The URL of this list at another offset, filter included: "" when neither has anything to say,
    /// otherwise a query string starting with "?". A handler name, when given, goes first.
    /// </summary>
    public abstract string LinkTo(int offset, string? handler = null);

    /// <summary>
    /// The one place the portal asks whether htmx made the request. htmx sends the header on every
    /// request it issues, boosted forms included, so a page whose edits reload wholesale still gets a page.
    /// </summary>
    public static bool IsHtmx(HttpRequest request) => request.Headers.ContainsKey("HX-Request");
}

/// <summary>
/// The shared page model behind the six administration lists. It reads the filter and the slice
/// wanted off the request, holds the rows the store returned, and makes the one decision about
/// what an htmx edit gets back: the table alone, because that is all htmx asked for.
/// </summary>
public abstract class AdminListPage<TFilter, TRow> : AdminListPage where TFilter : IListFilter<TFilter>
{
    private TFilter? _filter;
    private ListQuery? _query;

    public TFilter Filter => _filter ??= TFilter.Read(RequestValues());

    public ListQuery Query => _query ??= Paging?.Read(RequestValues()) ?? ListQuery.All;

    public Slice<TRow> Slice { get; protected set; } = Slice<TRow>.Empty;

    public override Paging? Paging => null;

    /// <summary>The partial htmx swaps after an edit, or null on a page whose edits reload the whole page.</summary>
    protected virtual string? TablePartial => null;

    public void OnGet() => Load();

    /// <summary>Fills <see cref="Slice"/>, and whatever else the page shows, from the stores.</summary>
    protected abstract void Load();

    /// <summary>
    /// After an edit: reload, then give htmx the table alone and a plain browser the page. The
    /// messages travel in the page, so the partial reads them from there.
    /// </summary>
    protected IActionResult Done()
    {
        Load();
        return TablePartial is not null && IsHtmx(Request) ? Table() : Page();
    }

    /// <summary>The table partial on its own, as an htmx swap.</summary>
    protected PartialViewResult Table()
    {
        var result = Partial(TablePartial ?? throw new InvalidOperationException("This page has no table partial."), Slice);
        result.ViewData[AdminListViewData.PageKey] = this;
        result.ViewData[AdminListViewData.SwapKey] = true;
        return result;
    }

    public override string LinkTo(int offset, string? handler = null)
    {
        var values = new Dictionary<string, string?>();
        values.Put("handler", handler);
        Filter.Write(values);
        Paging?.Write(values, offset);
        return QueryValues.ToQueryString(values);
    }

    [NonHandler]
    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        ViewData[AdminListViewData.PageKey] = this;
        base.OnPageHandlerExecuting(context);
    }

    /// <summary>
    /// The query string, with the form on top when there is one. A GET filter arrives on the URL; a
    /// decision posted from a row carries its tab and page as hidden fields, and both must be seen.
    /// Names compare like model binding does, without regard to case.
    /// </summary>
    private IReadOnlyDictionary<string, string?> RequestValues()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Request.Query)
        {
            values[key] = value.FirstOrDefault();
        }

        if (Request.HasFormContentType)
        {
            foreach (var (key, value) in Request.Form)
            {
                values[key] = value.FirstOrDefault();
            }
        }

        return values;
    }
}

/// <summary>How a table partial, whose model is only its slice, reaches the page behind it.</summary>
public static class AdminListViewData
{
    public const string PageKey = "AdminListPage";

    public const string SwapKey = "AdminListSwap";

    public static TPage Page<TPage>(this ViewDataDictionary viewData) where TPage : AdminListPage
        => viewData[PageKey] as TPage
           ?? throw new InvalidOperationException($"This partial was rendered outside a {typeof(TPage).Name}.");

    public static AdminListPage Page(this ViewDataDictionary viewData) => viewData.Page<AdminListPage>();

    /// <summary>True when this render is the fragment htmx swaps in, rather than part of a whole page.</summary>
    public static bool IsSwap(this ViewDataDictionary viewData) => viewData[SwapKey] is true;
}
