using AppPortal.Shared;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Reading the table winget prints for <c>list</c>. There is no machine-readable form of it, so the
/// columns are found from the header and every row is sliced at those offsets. Splitting on runs of
/// spaces would not do: a display name contains spaces, and so does a version on occasion.
/// </summary>
public static class WingetList
{
    /// <summary>What the table says is installed, or nothing at all when it cannot be read.</summary>
    public static IReadOnlyList<InstalledSoftware> Parse(string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n');
        var header = Array.FindIndex(lines, Header);
        if (header < 0)
        {
            return [];
        }

        var name = lines[header].IndexOf("Name", StringComparison.Ordinal);
        var id = lines[header].IndexOf("Id", StringComparison.Ordinal);
        var version = lines[header].IndexOf("Version", StringComparison.Ordinal);
        if (name < 0 || id <= name || version <= id)
        {
            return [];
        }

        var found = new List<InstalledSoftware>();
        foreach (var line in lines.Skip(header + 1))
        {
            // The rule the separator follows, and the one a progress spinner left on its own line
            // breaks. Anything that is not a row of the table is simply not a row of the table.
            if (line.Length <= id || line.TrimStart().StartsWith('-') || line.Trim().Length == 0)
            {
                continue;
            }

            var displayName = Slice(line, name, id).Trim();
            var found_version = Slice(line, version, line.Length).Trim();
            if (displayName.Length == 0)
            {
                continue;
            }

            // A name too long for its column is cut with an ellipsis, and half a name matched against
            // the catalog is worse than no name: it would claim software the device does not have.
            if (displayName.EndsWith('…'))
            {
                continue;
            }

            found.Add(new InstalledSoftware(displayName, FirstWord(found_version)));
        }

        return found;
    }

    private static bool Header(string line)
        => line.Contains("Name", StringComparison.Ordinal)
           && line.Contains("Id", StringComparison.Ordinal)
           && line.Contains("Version", StringComparison.Ordinal);

    private static string Slice(string line, int start, int end)
    {
        if (start >= line.Length)
        {
            return "";
        }

        return line[start..Math.Min(Math.Max(end, start), line.Length)];
    }

    /// <summary>
    /// The version column runs to the end of the line and carries the Available and Source columns
    /// with it when they are present, so only the first word of it is the version.
    /// </summary>
    private static string FirstWord(string text)
    {
        var space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}
