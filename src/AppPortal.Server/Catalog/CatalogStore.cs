using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Options;
using AppPortal.Shared;

using Microsoft.Extensions.Options;

namespace AppPortal.Server.Catalog;

public sealed class CatalogEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "Other";
    public string? IconUrl { get; set; }
    public bool Featured { get; set; }
    public Action1PackageRef Action1 { get; set; } = new();
    public MatchRule? Match { get; set; }

    public CatalogApp ToPublic() => new(Id, Name, Publisher, Description, Category, IconUrl, Featured);

    /// <summary>True when an inventory row names this app.</summary>
    public bool MatchesInstalled(string installedName)
    {
        if (Match?.NameEquals is { Length: > 0 } exact)
        {
            return string.Equals(installedName, exact, StringComparison.OrdinalIgnoreCase);
        }

        var needle = Match?.NameContains is { Length: > 0 } contains ? contains : Name;
        return installedName.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class Action1PackageRef
{
    /// <summary>Software Repository package ID, for example Google_Google_Chrome_1570243626751_builtin.</summary>
    public string PackageId { get; set; } = "";

    /// <summary>"latest" or an exact version string published in the repository.</summary>
    public string Version { get; set; } = "latest";
}

public sealed class MatchRule
{
    public string? NameContains { get; set; }
    public string? NameEquals { get; set; }
}

public sealed class CatalogFile
{
    public List<CatalogEntry> Apps { get; set; } = [];
}

/// <summary>Reads the catalog file and reloads it whenever its modification time changes.</summary>
public sealed class CatalogStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger<CatalogStore> _logger;
    private readonly object _gate = new();
    private DateTime _loadedStamp = DateTime.MinValue;
    private IReadOnlyList<CatalogEntry> _entries = [];

    public CatalogStore(IOptions<PortalOptions> options, IHostEnvironment env, ILogger<CatalogStore> logger)
    {
        _path = Path.IsPathRooted(options.Value.CatalogPath)
            ? options.Value.CatalogPath
            : Path.Combine(env.ContentRootPath, options.Value.CatalogPath);
        _logger = logger;
    }

    public string Path_ => _path;

    public IReadOnlyList<CatalogEntry> Entries
    {
        get
        {
            ReloadIfChanged();
            return _entries;
        }
    }

    public CatalogEntry? Find(string id)
        => Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<CatalogEntry> Parse(string json)
    {
        var file = JsonSerializer.Deserialize<CatalogFile>(json, Json) ?? new CatalogFile();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in file.Apps)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Name))
            {
                throw new InvalidDataException("Every catalog app needs an id and a name.");
            }

            if (string.IsNullOrWhiteSpace(entry.Action1.PackageId))
            {
                throw new InvalidDataException($"Catalog app '{entry.Id}' has no action1.packageId.");
            }

            if (!seen.Add(entry.Id))
            {
                throw new InvalidDataException($"Catalog app id '{entry.Id}' appears more than once.");
            }
        }

        return file.Apps;
    }

    private void ReloadIfChanged()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                if (_entries.Count > 0 || _loadedStamp == DateTime.MinValue)
                {
                    _logger.LogWarning("Catalog file {Path} not found; serving an empty catalog", _path);
                }

                _entries = [];
                _loadedStamp = DateTime.MaxValue;
                return;
            }

            var stamp = File.GetLastWriteTimeUtc(_path);
            if (stamp == _loadedStamp)
            {
                return;
            }

            try
            {
                _entries = Parse(File.ReadAllText(_path));
                _loadedStamp = stamp;
                _logger.LogInformation("Loaded {Count} catalog apps from {Path}", _entries.Count, _path);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                _logger.LogError(ex, "Catalog file {Path} is invalid; keeping the previous catalog", _path);
                _loadedStamp = stamp;
            }
        }
    }
}
