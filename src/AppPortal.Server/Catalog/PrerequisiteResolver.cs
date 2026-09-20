using AppPortal.Shared;

namespace AppPortal.Server.Catalog;

/// <summary>Why a set of prerequisites cannot be saved or expanded.</summary>
public sealed class PrerequisiteException(string message) : Exception(message);

/// <summary>
/// Turns "this app needs those apps first" into the list to install, in order. Depth first, each app
/// once, the app somebody actually asked for last.
/// </summary>
public static class PrerequisiteResolver
{
    /// <summary>
    /// How long a chain may be. A chain this long is a mistake in the catalog rather than a fleet
    /// somebody meant to build, and finding out at install time is the worst moment to find out.
    /// </summary>
    public const int MaxChain = 10;

    /// <summary>
    /// The apps to install so that <paramref name="app"/> can run, ending with it. Anything already on
    /// the device is left out: installing a launcher somebody already has wastes their afternoon.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Expand(
        CatalogEntry app,
        IReadOnlyDictionary<string, CatalogEntry> byId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> prerequisites,
        Func<CatalogEntry, bool> alreadyInstalled)
    {
        var ordered = new List<CatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var onPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(app, byId, prerequisites, seen, onPath, ordered);

        // The asked-for app is never dropped, however installed the device thinks it is: somebody
        // pressed the button, and refusing to do the one thing they asked for helps nobody.
        var chain = ordered.Where(entry => entry.Id == app.Id || !alreadyInstalled(entry)).ToList();
        if (chain.Count > MaxChain)
        {
            throw new PrerequisiteException($"{app.Name} needs {chain.Count} other apps installed first, which is more than the {MaxChain} allowed.");
        }

        return chain;
    }

    private static void Visit(
        CatalogEntry app,
        IReadOnlyDictionary<string, CatalogEntry> byId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> prerequisites,
        HashSet<string> seen,
        HashSet<string> onPath,
        List<CatalogEntry> ordered)
    {
        if (!seen.Add(app.Id))
        {
            return;
        }

        if (!onPath.Add(app.Id))
        {
            throw new PrerequisiteException($"{app.Name} needs itself, directly or through another app.");
        }

        if (prerequisites.TryGetValue(app.Id, out var needs))
        {
            foreach (var id in needs)
            {
                if (!byId.TryGetValue(id, out var required))
                {
                    throw new PrerequisiteException($"{app.Name} needs '{id}', which is not in the catalog.");
                }

                Visit(required, byId, prerequisites, seen, onPath, ordered);
            }
        }

        onPath.Remove(app.Id);
        ordered.Add(app);
    }

    /// <summary>
    /// Checks that adding these prerequisites to an app leaves no loop. Refused when the edge is saved
    /// rather than when somebody installs, because the administrator who made the loop is the one who
    /// can undo it, and the person clicking Install is not.
    /// </summary>
    public static void EnsureNoCycle(string appId, IReadOnlyList<string> needs,
        IReadOnlyDictionary<string, IReadOnlyList<string>> existing,
        IReadOnlyDictionary<string, CatalogEntry> byId)
    {
        var edges = existing.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        edges[appId] = needs;

        var path = new List<string>();
        var onPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(string id)
        {
            if (done.Contains(id))
            {
                return;
            }

            if (!onPath.Add(id))
            {
                var from = path.IndexOf(path.Last(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase)));
                var loop = path.Skip(from < 0 ? 0 : from).Append(id).Select(x => byId.TryGetValue(x, out var e) ? e.Name : x);
                throw new PrerequisiteException("These apps would need each other in a loop: " + string.Join(" needs ", loop) + ".");
            }

            path.Add(id);
            if (edges.TryGetValue(id, out var required))
            {
                foreach (var next in required)
                {
                    Walk(next);
                }
            }

            path.RemoveAt(path.Count - 1);
            onPath.Remove(id);
            done.Add(id);
        }

        Walk(appId);
    }
}
