namespace AppPortal.Server.Admin.Lists;

/// <summary>
/// The slice of a list a caller wants. A null <see cref="Limit"/> is the whole list, which is what the
/// lists nobody pages ask for. A null <see cref="Sort"/> is the store's own ordering. The total is
/// counted only when <see cref="WantTotal"/> is set, because it is a second query over the same rows.
/// </summary>
public sealed record ListQuery(int? Limit, int Offset, string? Sort, bool WantTotal)
{
    /// <summary>Every row, in the store's own order.</summary>
    public static readonly ListQuery All = new(null, 0, null, false);

    public int? Limit { get; init; } = Limit is null or > 0
        ? Limit
        : throw new ArgumentOutOfRangeException(nameof(Limit), Limit, "A limit is at least one row.");

    public int Offset { get; init; } = Offset >= 0
        ? Offset
        : throw new ArgumentOutOfRangeException(nameof(Offset), Offset, "An offset is never negative.");
}
