using System.Text.Json;
using System.Text.RegularExpressions;

using AppPortal.Shared;

namespace AppPortal.Agent.Executors;

/// <summary>
/// Reads what one package manager says it has installed. Nothing a manager installs writes an
/// Uninstall registry entry, so winget cannot see any of it, and without this a card for a Scoop app
/// never turns to Installed and a Chocolatey prerequisite is installed again on every chain.
/// </summary>
/// <remarks>
/// Each row is the package id as the manager spells it, because that is what the catalog stores and
/// what the server matches first. The process's error stream is mixed into its output, so the JSON
/// readers look for the document inside the text rather than expecting the text to be one.
/// </remarks>
public static partial class ManagerList
{
    /// <summary>
    /// What <paramref name="output"/> lists, or null when this is not a manager this build can read.
    /// An empty list is a real answer: a manager with nothing installed through it.
    /// </summary>
    public static IReadOnlyList<InstalledSoftware>? Parse(string manager, string output) => manager switch
    {
        "choco" => Rows(output, ChocoRow()),
        "scoop" or "dotnet-tool" => Table(output),
        "npm" => NpmJson(output),
        "yarn" => Rows(output, YarnRow()),
        "bun" => Tree(output),
        "pip" => PipJson(output),
        "cargo" => Rows(output, CargoRow()),
        "vcpkg" => Rows(output, VcpkgRow()),
        "powershell-module" or "powershell5-module" => Rows(output, CsvRow())
            .Where(row => row is not { Name: "Name", Version: "Version" }).ToList(),
        _ => null,
    };

    private static IEnumerable<string> Lines(string output)
        => output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r'));

    private static List<InstalledSoftware> Rows(string output, Regex row) => Lines(output)
        .Select(line => row.Match(line.Trim()))
        .Where(match => match.Success)
        .Select(match => new InstalledSoftware(match.Groups["name"].Value, match.Groups["version"].Value))
        .ToList();

    /// <summary>A header, a rule of dashes, then one row per package: id first, version second.</summary>
    private static List<InstalledSoftware> Table(string output)
    {
        var found = new List<InstalledSoftware>();
        var pastRule = false;
        foreach (var line in Lines(output))
        {
            var text = line.Trim();
            if (!pastRule)
            {
                pastRule = text.Length > 0 && text.All(c => c is '-' or ' ');
                continue;
            }

            var cells = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length >= 2)
            {
                found.Add(new InstalledSoftware(cells[0], cells[1]));
            }
        }

        return found;
    }

    /// <summary>Bun draws a tree: one line per package, <c>name@version</c> after the branch.</summary>
    private static List<InstalledSoftware> Tree(string output)
    {
        var found = new List<InstalledSoftware>();
        foreach (var line in Lines(output))
        {
            var branch = line.IndexOf("── ", StringComparison.Ordinal) is var box and >= 0
                ? box + 3
                : line.IndexOf("-- ", StringComparison.Ordinal) is var ascii and >= 0 ? ascii + 3 : -1;
            if (branch < 0)
            {
                continue;
            }

            var package = line[branch..].Trim();
            // After the first character, so a scoped package's own @ is not taken for the separator.
            var at = package.LastIndexOf('@');
            if (at > 0)
            {
                found.Add(new InstalledSoftware(package[..at], package[(at + 1)..]));
            }
        }

        return found;
    }

    private static List<InstalledSoftware>? NpmJson(string output)
    {
        using var document = Json(output, '{', '}');
        if (document is null)
        {
            return null;
        }

        if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)
            || dependencies.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return dependencies.EnumerateObject()
            .Select(package => new InstalledSoftware(package.Name,
                package.Value.ValueKind == JsonValueKind.Object && package.Value.TryGetProperty("version", out var version)
                    ? version.GetString() ?? ""
                    : ""))
            .ToList();
    }

    private static List<InstalledSoftware>? PipJson(string output)
    {
        using var document = Json(output, '[', ']');
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return document.RootElement.EnumerateArray()
            .Where(package => package.ValueKind == JsonValueKind.Object && package.TryGetProperty("name", out _))
            .Select(package => new InstalledSoftware(package.GetProperty("name").GetString() ?? "",
                package.TryGetProperty("version", out var version) ? version.GetString() ?? "" : ""))
            .ToList();
    }

    private static JsonDocument? Json(string output, char open, char close)
    {
        var start = output.IndexOf(open);
        var end = output.LastIndexOf(close);
        if (start < 0 || end < start)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(output.AsMemory(start, end - start + 1));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary><c>git|2.46.0</c>, which is what <c>--limit-output</c> is for.</summary>
    [GeneratedRegex(@"\A(?<name>[^|\s]+)\|(?<version>[^|\s]+)\z")]
    private static partial Regex ChocoRow();

    /// <summary><c>info "typescript@5.5.4" has binaries:</c></summary>
    [GeneratedRegex("""\Ainfo "(?<name>@?[^@"]+)@(?<version>[^"]+)" has binaries""")]
    private static partial Regex YarnRow();

    /// <summary><c>ripgrep v14.1.0:</c>, sometimes with the source in brackets before the colon.</summary>
    [GeneratedRegex(@"\A(?<name>\S+) v(?<version>[^\s:]+)(?: \([^)]*\))?:\z")]
    private static partial Regex CargoRow();

    /// <summary>
    /// <c>zlib:x64-windows   1.3.1   A compression library</c>. A feature's own row, <c>curl[ssl]:...</c>,
    /// has no version and does not match: the port's row already says it is there.
    /// </summary>
    [GeneratedRegex(@"\A(?<name>[a-z0-9][a-z0-9-]*):\S+\s+(?<version>\S+)")]
    private static partial Regex VcpkgRow();

    /// <summary><c>"PSReadLine","2.3.5"</c>, from <c>ConvertTo-Csv</c>.</summary>
    [GeneratedRegex("""\A"(?<name>[^"]+)","(?<version>[^"]*)"\z""")]
    private static partial Regex CsvRow();
}
