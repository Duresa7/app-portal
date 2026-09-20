namespace AppPortal.Server.Admin.Lists;

/// <summary>The filter of a list that is never narrowed: enrollment keys and administrators.</summary>
public sealed record NoFilter : IListFilter<NoFilter>
{
    public static readonly NoFilter Instance = new();

    public void Write(IDictionary<string, string?> query)
    {
    }

    public static NoFilter Read(IReadOnlyDictionary<string, string?> query) => Instance;
}
