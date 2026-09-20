namespace AppPortal.Server.Admin.Lists;

/// <summary>
/// One search term, the way the catalog and device lists are narrowed. Which fields the term is
/// held against is the store's business; the filter only knows how to compare.
/// </summary>
public sealed record SearchFilter(string? Term) : IListFilter<SearchFilter>
{
    public static readonly SearchFilter None = new(Term: null);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Term);

    /// <summary>True when any of the fields contains the term, or when there is no term.</summary>
    public bool Matches(params string?[] fields)
    {
        if (IsEmpty)
        {
            return true;
        }

        var needle = Term!.Trim();
        return fields.Any(field => field is not null && field.Contains(needle, StringComparison.OrdinalIgnoreCase));
    }

    public void Write(IDictionary<string, string?> query) => query.Put("Search", Term);

    public static SearchFilter Read(IReadOnlyDictionary<string, string?> query) => new(query.Get("Search"));
}
