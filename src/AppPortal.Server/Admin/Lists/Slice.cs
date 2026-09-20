namespace AppPortal.Server.Admin.Lists;

/// <summary>
/// What a list query returned: the rows, the offset they start at, whether more follow, and the
/// total when it was asked for. Every administration table partial renders one of these.
/// </summary>
public sealed record Slice<T>(IReadOnlyList<T> Rows, int Offset, bool HasMore, int? Total)
{
    public static readonly Slice<T> Empty = new([], 0, false, null);
}

/// <summary>The two ways a store turns what it read into a <see cref="Slice{T}"/>.</summary>
public static class Slice
{
    /// <summary>
    /// Cuts the slice out of a list read in full. For the stores that filter in memory, and for the
    /// lists nobody pages, where the whole list is what was read anyway.
    /// </summary>
    public static Slice<T> Of<T>(IReadOnlyList<T> all, ListQuery query)
    {
        var start = Math.Min(query.Offset, all.Count);
        var end = query.Limit is { } limit ? Math.Min(start + limit, all.Count) : all.Count;
        var rows = new List<T>(end - start);
        for (var i = start; i < end; i++)
        {
            rows.Add(all[i]);
        }

        return new Slice<T>(rows, query.Offset, end < all.Count, query.WantTotal ? all.Count : null);
    }

    /// <summary>
    /// How many rows to ask the database for: one past the limit, so the slice learns whether more
    /// follow without a second query. Null when the whole list is wanted; SQLite reads "LIMIT -1" as no limit.
    /// </summary>
    public static int Lookahead(ListQuery query) => query.Limit is { } limit ? limit + 1 : -1;

    /// <summary>Builds the slice from a window read with <see cref="Lookahead"/>, dropping the extra row.</summary>
    public static Slice<T> FromLookahead<T>(IReadOnlyList<T> window, ListQuery query, int? total)
    {
        if (query.Limit is not { } limit || window.Count <= limit)
        {
            return new Slice<T>(window, query.Offset, false, total);
        }

        return new Slice<T>([.. window.Take(limit)], query.Offset, true, total);
    }
}
