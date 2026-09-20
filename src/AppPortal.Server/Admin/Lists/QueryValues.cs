namespace AppPortal.Server.Admin.Lists;

/// <summary>The query-string dictionary a filter writes to and reads from, and the string it becomes.</summary>
public static class QueryValues
{
    /// <summary>Writes a value, or nothing when it is blank: a blank field says nothing and should not lengthen the link.</summary>
    public static void Put(this IDictionary<string, string?> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query[name] = value;
        }
    }

    /// <summary>The value under a name, or null when it is missing or blank.</summary>
    public static string? Get(this IReadOnlyDictionary<string, string?> query, string name)
        => query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    /// <summary>"?a=1&amp;b=2" in the order the values were written, or "" when nothing was.</summary>
    public static string ToQueryString(IEnumerable<KeyValuePair<string, string?>> query)
    {
        var parts = query
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key + "=" + Uri.EscapeDataString(pair.Value!))
            .ToList();
        return parts.Count == 0 ? "" : "?" + string.Join("&", parts);
    }
}
