using System.Globalization;

namespace AppPortal.Server.Admin.Lists;

/// <summary>
/// How a page names its place in the list on the URL and how many rows it shows at once. Installs
/// count rows ("Skip=100") and requests count pages ("p=3"); both notations stay because they are the
/// links administrators already hold. A page that does not page has no <see cref="Paging"/> at all.
/// </summary>
public sealed record Paging(string Parameter, int PageSize, bool CountsPages, bool WantTotal)
{
    public static Paging ByRows(string parameter, int pageSize, bool wantTotal = false)
        => new(parameter, Positive(pageSize), CountsPages: false, wantTotal);

    public static Paging ByPages(string parameter, int pageSize, bool wantTotal = false)
        => new(parameter, Positive(pageSize), CountsPages: true, wantTotal);

    /// <summary>The query for the place the URL names. Anything that is not a whole number is the start.</summary>
    public ListQuery Read(IReadOnlyDictionary<string, string?> values)
    {
        var number = int.TryParse(values.Get(Parameter), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
        var offset = CountsPages ? Math.Max(0, number - 1) * PageSize : number;
        return new ListQuery(PageSize, offset, null, WantTotal);
    }

    /// <summary>Names an offset on the URL. The start of the list is the URL without the parameter.</summary>
    public void Write(IDictionary<string, string?> values, int offset)
    {
        if (offset <= 0)
        {
            return;
        }

        var number = CountsPages ? PageNumber(offset) : offset;
        values[Parameter] = number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The one-based page an offset falls on.</summary>
    public int PageNumber(int offset) => Math.Max(0, offset) / PageSize + 1;

    private static int Positive(int pageSize)
        => pageSize > 0 ? pageSize : throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "A page holds at least one row.");
}
