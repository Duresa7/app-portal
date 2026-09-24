namespace AppPortal.Agent.Jobs;

/// <summary>
/// Directories put ahead of PATH for one child process, either as the PATH string itself or inside the
/// variables of a Windows environment block. Plain string work, kept apart from the two launchers that
/// use it so that it is tested on any operating system.
/// </summary>
public static class SearchPath
{
    /// <summary>PATH with <paramref name="first"/> in front of it, or only those when there was no PATH.</summary>
    public static string Prepend(IReadOnlyList<string> first, string? path)
        => string.Join(';', string.IsNullOrEmpty(path) ? first : first.Append(path));

    /// <summary>
    /// An environment block's variables, each NAME=value, with <paramref name="first"/> ahead of PATH.
    /// Windows spells it Path and compares names without case, and so does this. The entries that start
    /// with '=' are the per-drive current directories; the name after that first character is not a
    /// variable anybody set, so they are passed through untouched.
    /// </summary>
    public static IReadOnlyList<string> WithPathFirst(IReadOnlyList<string> variables, IReadOnlyList<string> first)
    {
        var result = new List<string>(variables.Count + 1);
        var found = false;
        foreach (var variable in variables)
        {
            var equals = variable.Length > 1 ? variable.IndexOf('=', 1) : -1;
            if (!found && equals > 0 && variable.AsSpan(0, equals).Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(variable[..(equals + 1)] + Prepend(first, variable[(equals + 1)..]));
                found = true;
                continue;
            }

            result.Add(variable);
        }

        if (!found)
        {
            result.Add("Path=" + Prepend(first, null));
        }

        return result;
    }
}
