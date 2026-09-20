namespace AppPortal.Server.Admin.Lists;

/// <summary>
/// The columns a store lets a list be sorted by, each under the name a caller uses, and the ordering
/// it applies when nobody asks. A name that was not declared is refused; nothing a caller sends is
/// ever interpolated into SQL.
/// </summary>
public sealed class SortColumns
{
    private readonly string _default;
    private readonly Dictionary<string, string> _columns;

    public SortColumns(string defaultOrder, params (string Name, string Column)[] columns)
    {
        _default = defaultOrder;
        _columns = columns.ToDictionary(c => c.Name, c => c.Column, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> Names => _columns.Keys;

    /// <summary>
    /// The ORDER BY clause for a query's sort: a declared name ascending, "-name" descending, or the
    /// store's default when the sort is null. The default follows as a tie-breaker so that paging over
    /// equal values never repeats or skips a row.
    /// </summary>
    public string OrderBy(string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort))
        {
            return " ORDER BY " + _default;
        }

        var descending = sort[0] == '-';
        var name = descending ? sort[1..] : sort;
        if (!_columns.TryGetValue(name, out var column))
        {
            throw new UnknownSortException(sort, Names);
        }

        return " ORDER BY " + column + (descending ? " DESC, " : " ASC, ") + _default;
    }
}

/// <summary>A sort named a column the store does not sort by.</summary>
public sealed class UnknownSortException(string sort, IEnumerable<string> known)
    : ArgumentException($"'{sort}' is not a column this list sorts by. Choose one of: {string.Join(", ", known)}.")
{
    public string Sort { get; } = sort;
}
