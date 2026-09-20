namespace AppPortal.Server.Admin.Lists;

/// <summary>
/// A filter writes its own fields to a query string and reads them back, so each field name is
/// declared in the filter and nowhere else. The store that owns the list knows what the fields
/// mean; the list module and the page carry the filter without looking inside.
/// </summary>
public interface IListFilter<TSelf> where TSelf : IListFilter<TSelf>
{
    void Write(IDictionary<string, string?> query);

    static abstract TSelf Read(IReadOnlyDictionary<string, string?> query);
}
