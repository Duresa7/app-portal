using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Admin.Lists;
using AppPortal.Server.Data;
using AppPortal.Server.Options;
using AppPortal.Shared;

using Microsoft.Data.Sqlite;
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

    /// <summary>Hidden apps stay in the catalog and keep their history but are not offered to devices.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hidden { get; set; }

    /// <summary>
    /// The engine this app prefers when a device could use either, or null to follow the server. It
    /// decides nothing on a device that has only one of them.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EngineOverride { get; set; }

    /// <summary>
    /// What this app needs that the portal cannot arrange, in words for the person to read. Never
    /// checked and never a reason to refuse an install; see migration 015.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Requirements { get; set; }

    /// <summary>
    /// Catalog apps that must be installed before this one, in the order they should go on. Held on
    /// the entry so that an export carries a chain and an import rebuilds it.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public List<string> Requires { get; set; } = [];

    /// <summary>
    /// Whether the person who installed this may take it off again. Off unless an administrator says
    /// otherwise: the safe answer for anything nobody has thought about is that only they can.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool UserRemovable { get; set; }

    public Action1PackageRef Action1 { get; set; } = new();
    public PackageDefinition? Agent { get; set; }
    public MatchRule? Match { get; set; }

    /// <summary>The contract, with the engine this device would use and nothing about any other.</summary>
    public CatalogApp ToPublic(string? engine) => ToPublic() with { Engine = engine };

    public CatalogApp ToPublic() => new(Id, Name, Publisher, Description, Category, IconUrl, Featured,
        (HasAction1, Agent is not null) switch
        {
            (true, true) => ["action1", "agent"],
            (true, false) => ["action1"],
            (false, true) => ["agent"],
            _ => [],
        },
        Agent?.DownloadSizeBytes,
        // Only an agent package can be per-user; Action1 always installs for the whole device.
        Agent?.Scope,
        null,
        Requirements,
        UserRemovable);

    [JsonIgnore]
    public bool HasAction1 => !string.IsNullOrWhiteSpace(Action1?.PackageId);

    /// <summary>
    /// True when an inventory row names this app. A package manager's row is matched on the app's own
    /// package id first, because that is how the manager lists it, and on the name after that like any
    /// other row. <paramref name="source"/> is what found the row: <c>winget</c>, a manager, or null.
    /// </summary>
    public bool MatchesInstalled(string installedName, string? source = null)
    {
        if (Agent is ManagedPackageDefinition managed
            && string.Equals(source, managed.Manager, StringComparison.Ordinal)
            && PackageManagers.Find(managed.Manager) is { } manager
            && string.Equals(installedName, manager.ListedAs(managed.Id), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Match?.NameEquals is { Length: > 0 } exact)
        {
            return string.Equals(installedName, exact, StringComparison.OrdinalIgnoreCase);
        }

        var needle = Match?.NameContains is { Length: > 0 } contains ? contains : Name;
        return installedName.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where the agent gets <paramref name="agent"/> from, named the way the catalog page names it, or
    /// empty for none. The page's source list, its sentence and the catalog list all say it this way.
    /// </summary>
    public static string SourceName(PackageDefinition? agent) => agent switch
    {
        WingetPackageDefinition { Source: WingetSources.Store } => "Microsoft Store",
        WingetPackageDefinition => "winget",
        DirectPackageDefinition => "Direct download",
        ManagedPackageDefinition managed => PackageManagers.Find(managed.Manager)?.DisplayName ?? managed.Manager,
        _ => "",
    };

    /// <summary>
    /// What saving this app will do, in one sentence: the app, who it installs for, and where it comes
    /// from. An administrator reads this rather than reassembling it from six fields.
    /// </summary>
    public string Describe()
    {
        var name = string.IsNullOrWhiteSpace(Name) ? "this app" : Name.Trim();
        if (Agent is null)
        {
            return HasAction1
                ? $"Installs {name} for everyone on the PC, through Action1."
                : $"{name} has no source yet, so no device can install it.";
        }

        var agent = $"{For(Agent.Scope)}, {Through(Agent)}";
        if (!HasAction1)
        {
            return $"Installs {name} {agent}.";
        }

        var either = EngineOverride switch
        {
            EngineLabel.Action1 => " A device that could use either uses Action1.",
            EngineLabel.Agent => " A device that could use either uses the agent.",
            _ => "",
        };
        return $"Installs {name} for everyone on the PC through Action1, or {agent} on a device with only the agent.{either}";

        static string For(string scope) => scope == "user" ? "for the person who asks for it" : "for everyone on the PC";

        static string Through(PackageDefinition agent) => agent switch
        {
            WingetPackageDefinition { Source: WingetSources.Store } => "from the Microsoft Store",
            DirectPackageDefinition direct => "with its own installer from "
                                              + (Uri.TryCreate(direct.Url, UriKind.Absolute, out var url) ? url.Host : "its download address"),
            _ => "through " + SourceName(agent),
        };
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

/// <summary>
/// The catalog, held in the database. `catalog.json` seeds an empty database once and is the format
/// `catalog import` and `catalog export` read and write; after that the database is what the API serves.
/// </summary>
public sealed class CatalogStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // A catalog is written by hand or by a script, and neither keeps "kind" first in an agent
        // definition. Without this, a kind anywhere else reads as no kind at all.
        AllowOutOfOrderMetadataProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions Export = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Database _database;

    public CatalogStore(Database database, IOptions<PortalOptions> options, IHostEnvironment env)
    {
        _database = database;
        SeedPath = Path.IsPathRooted(options.Value.CatalogPath)
            ? options.Value.CatalogPath
            : Path.Combine(env.ContentRootPath, options.Value.CatalogPath);
    }

    public CatalogStore(Database database, string seedPath)
    {
        _database = database;
        SeedPath = seedPath;
    }

    /// <summary>The `catalog.json` this deployment seeds and imports from. Not read once the database holds apps.</summary>
    public string SeedPath { get; }

    public IReadOnlyList<CatalogEntry> Entries
    {
        get
        {
            using var connection = _database.Open();
            return Read(connection, null);
        }
    }

    /// <summary>What the device API offers: everything the administrator has not hidden.</summary>
    public IReadOnlyList<CatalogEntry> VisibleEntries
    {
        get
        {
            using var connection = _database.Open();
            return Read(connection, null, visibleOnly: true);
        }
    }

    public CatalogEntry? Find(string id)
    {
        using var connection = _database.Open();
        return Read(connection, id).FirstOrDefault();
    }

    /// <summary>
    /// Apps whose id, name, publisher or category contains the search term, in catalog order unless the
    /// query sorts otherwise. The catalog is small and the match is a substring, so it is read whole
    /// and narrowed here rather than in SQL.
    /// </summary>
    public Slice<CatalogEntry> List(SearchFilter filter, ListQuery query)
    {
        var orderBy = Sorts.OrderBy(query.Sort);
        using var connection = _database.Open();
        var entries = Read(connection, null, orderBy: orderBy);
        var matching = filter.IsEmpty
            ? entries
            : entries.Where(e => filter.Matches(e.Id, e.Name, e.Publisher, e.Category)).ToList();
        return Slice.Of(matching, query);
    }

    public int Count()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM catalog_apps;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public static IReadOnlyList<CatalogEntry> Parse(string json)
    {
        CatalogFile file;
        try
        {
            file = JsonSerializer.Deserialize<CatalogFile>(json, Json) ?? new CatalogFile();
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidDataException($"An agent definition needs a kind of {PackageDefinition.Kinds}.", ex);
        }

        Validate(file.Apps);
        return file.Apps;
    }

    /// <summary>
    /// Upserts by id. Apps the file does not mention are left alone, but every field of the ones it does
    /// mention comes from the file, <c>hidden</c> included: an export is meant to restore what it captured,
    /// so a file that omits the flag says the app is visible. Seeding only runs on an empty catalog, so a
    /// restart never walks over an administrator's decision; an upload is a deliberate act.
    /// The file is checked whole before anything is written, and one bad app writes none of it.
    /// </summary>
    public int Import(IReadOnlyList<CatalogEntry> entries)
    {
        Validate(entries);
        var ids = EnsurePrerequisites(entries);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            var now = SqlTime.Now();
            using (var app = connection.CreateCommand())
            {
                app.Transaction = transaction;
                app.CommandText = """
                    INSERT INTO catalog_apps (id, name, publisher, description, category, icon_url, featured, hidden, match_json, engine_override, requirements, user_removable, created_at, updated_at)
                    VALUES (@id, @name, @publisher, @description, @category, @icon, @featured, @hidden, @match, @engine, @requirements, @removable, @now, @now)
                    ON CONFLICT(id) DO UPDATE SET
                        name = excluded.name, publisher = excluded.publisher, description = excluded.description,
                        category = excluded.category, icon_url = excluded.icon_url, featured = excluded.featured,
                        hidden = excluded.hidden, match_json = excluded.match_json,
                        engine_override = excluded.engine_override, requirements = excluded.requirements,
                        user_removable = excluded.user_removable,
                        updated_at = excluded.updated_at;
                    """;
                app.Parameters.AddWithValue("@id", entry.Id.Trim());
                app.Parameters.AddWithValue("@engine", (object?)Engine(entry.EngineOverride) ?? DBNull.Value);
                app.Parameters.AddWithValue("@requirements", (object?)entry.Requirements ?? DBNull.Value);
                app.Parameters.AddWithValue("@removable", entry.UserRemovable ? 1 : 0);
                app.Parameters.AddWithValue("@name", entry.Name);
                app.Parameters.AddWithValue("@publisher", entry.Publisher ?? "");
                app.Parameters.AddWithValue("@description", entry.Description ?? "");
                app.Parameters.AddWithValue("@category", entry.Category ?? "");
                app.Parameters.AddWithValue("@icon", (object?)entry.IconUrl ?? DBNull.Value);
                app.Parameters.AddWithValue("@featured", entry.Featured ? 1 : 0);
                app.Parameters.AddWithValue("@hidden", entry.Hidden ? 1 : 0);
                app.Parameters.AddWithValue("@match", entry.Match is null ? DBNull.Value : JsonSerializer.Serialize(entry.Match, Json));
                app.Parameters.AddWithValue("@now", now);
                app.ExecuteNonQuery();
            }

            WritePackages(connection, transaction, entry);
        }

        // Every app first, then what they need: an app may need one further down the same file, and
        // the prerequisite's row has to exist before the foreign key will take an edge to it.
        foreach (var entry in entries)
        {
            ReplacePrerequisites(connection, transaction, entry.Id.Trim(), Stored(entry, ids));
        }

        transaction.Commit();
        return entries.Count;
    }

    /// <summary>
    /// Writes one app, as the edit form and <c>PUT</c> send it. The same checks as an import of a file
    /// holding only this app, so an app saves through every route or through none.
    /// </summary>
    public void Upsert(CatalogEntry entry)
    {
        Validate([entry]);
        var ids = EnsurePrerequisites([entry]);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var now = SqlTime.Now();

        using (var app = connection.CreateCommand())
        {
            app.Transaction = transaction;
            app.CommandText = """
                INSERT INTO catalog_apps (id, name, publisher, description, category, icon_url, featured, hidden, match_json, engine_override, requirements, user_removable, created_at, updated_at)
                VALUES (@id, @name, @publisher, @description, @category, @icon, @featured, @hidden, @match, @engine, @requirements, @removable, @now, @now)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, publisher = excluded.publisher, description = excluded.description,
                    category = excluded.category, icon_url = excluded.icon_url, featured = excluded.featured,
                    hidden = excluded.hidden, match_json = excluded.match_json,
                    engine_override = excluded.engine_override, requirements = excluded.requirements,
                    user_removable = excluded.user_removable,
                    updated_at = excluded.updated_at;
                """;
            app.Parameters.AddWithValue("@id", entry.Id.Trim());
            app.Parameters.AddWithValue("@engine", (object?)Engine(entry.EngineOverride) ?? DBNull.Value);
            app.Parameters.AddWithValue("@requirements", (object?)entry.Requirements ?? DBNull.Value);
            app.Parameters.AddWithValue("@removable", entry.UserRemovable ? 1 : 0);
            app.Parameters.AddWithValue("@name", entry.Name.Trim());
            app.Parameters.AddWithValue("@publisher", entry.Publisher ?? "");
            app.Parameters.AddWithValue("@description", entry.Description ?? "");
            app.Parameters.AddWithValue("@category", entry.Category ?? "");
            app.Parameters.AddWithValue("@icon", string.IsNullOrWhiteSpace(entry.IconUrl) ? DBNull.Value : entry.IconUrl);
            app.Parameters.AddWithValue("@featured", entry.Featured ? 1 : 0);
            app.Parameters.AddWithValue("@hidden", entry.Hidden ? 1 : 0);
            app.Parameters.AddWithValue("@match", entry.Match is null ? DBNull.Value : JsonSerializer.Serialize(entry.Match, Json));
            app.Parameters.AddWithValue("@now", now);
            app.ExecuteNonQuery();
        }

        WritePackages(connection, transaction, entry);
        ReplacePrerequisites(connection, transaction, entry.Id.Trim(), Stored(entry, ids));

        transaction.Commit();
    }

    public bool SetHidden(string id, bool hidden)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE catalog_apps SET hidden = @hidden, updated_at = @now WHERE id = @id COLLATE NOCASE;";
        command.Parameters.AddWithValue("@hidden", hidden ? 1 : 0);
        command.Parameters.AddWithValue("@now", SqlTime.Now());
        command.Parameters.AddWithValue("@id", id);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Removes an app outright. False when an install refers to it: that history names the app id, and
    /// deleting the row would leave the installs page describing something that no longer exists.
    /// Hiding is the answer in that case.
    /// </summary>
    public bool Delete(string id)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var referenced = connection.CreateCommand())
        {
            referenced.Transaction = transaction;
            referenced.CommandText = "SELECT EXISTS(SELECT 1 FROM installs WHERE app_id = @id COLLATE NOCASE);";
            referenced.Parameters.AddWithValue("@id", id);
            if (Convert.ToInt64(referenced.ExecuteScalar()) != 0)
            {
                return false;
            }
        }

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            // catalog_packages cascades on the foreign key.
            delete.CommandText = "DELETE FROM catalog_apps WHERE id = @id COLLATE NOCASE;";
            delete.Parameters.AddWithValue("@id", id);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Ids that a route takes before an app can: <c>/admin/catalog/new</c> is the create form, and
    /// <c>GET /api/v1/admin/catalog/export</c> is the export. An app with either id could be written and
    /// then never opened by id again. An app stored under one before the rule is left where it is: it
    /// is in the list and the export, devices are still offered it, and it can be hidden or deleted by
    /// id. Saving it again under that id is refused, so it has to be saved under another.
    /// </summary>
    private static readonly Dictionary<string, string> ReservedIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["new"] = "the create form",
        ["export"] = "the catalog export",
    };

    /// <summary>
    /// What every app must be on its own, and that no id appears twice. The one place these rules live:
    /// a file, a form and a <c>PUT</c> all come through here, so none of them can hold an app another
    /// would refuse. Throws <see cref="InvalidDataException"/> naming the first fault. The prerequisites
    /// need the stored catalog as well, so <see cref="EnsurePrerequisites"/> checks those.
    /// </summary>
    private static void Validate(IReadOnlyList<CatalogEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            Validate(entry);

            if (!seen.Add(entry.Id.Trim()))
            {
                throw new InvalidDataException($"Catalog app id '{entry.Id}' appears more than once.");
            }
        }
    }

    private static void Validate(CatalogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            throw new InvalidDataException("An app needs an id.");
        }

        if (ReservedIds.TryGetValue(entry.Id.Trim(), out var owner))
        {
            throw new InvalidDataException($"'{entry.Id.Trim()}' is reserved for {owner}. Give the app another id.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidDataException("An app needs a name.");
        }

        // The form and the client stop at the same length; an import or a PUT that went past it would
        // store a note the form then refuses to save, and the app could not be edited without losing it.
        if ((entry.Requirements?.Length ?? 0) > CatalogLimits.MaxRequirementsLength)
        {
            throw new InvalidDataException($"Requirements must be {CatalogLimits.MaxRequirementsLength} characters or fewer.");
        }

        if (!string.IsNullOrWhiteSpace(entry.EngineOverride) && Engine(entry.EngineOverride) is not (EngineLabel.Action1 or EngineLabel.Agent))
        {
            // Anything else would be stored and then ignored by the engine choice without a word, which
            // reads as a preference that is being honoured when it is not.
            throw new InvalidDataException($"engineOverride must be {EngineLabel.Action1}, {EngineLabel.Agent}, or empty to follow the server, not '{entry.EngineOverride}'.");
        }

        if (!entry.HasAction1 && entry.Agent is null)
        {
            throw new InvalidDataException("An app needs an Action1 package id or an agent package.");
        }

        entry.Agent?.Validate();
    }

    /// <summary>
    /// The engine override as it is stored: lower case, as the form's list names it, or null for a
    /// blank. A file saying <c>Agent</c> means the same as the form saying <c>agent</c>, and the form
    /// has to find its own option again when it opens the app.
    /// </summary>
    private static string? Engine(string? engineOverride)
        => string.IsNullOrWhiteSpace(engineOverride) ? null : engineOverride.Trim().ToLowerInvariant();

    /// <summary>
    /// Refuses prerequisites that name no app or close a loop, judged on the catalog as it will be once
    /// <paramref name="incoming"/> is written: the stored apps with every incoming one laid over them. A
    /// file is judged as one set, so an app may need one later in the same file, and an app the file
    /// names takes its prerequisites from the file rather than from what is stored. Refused on save
    /// rather than on install, because the administrator who made the loop is the one who can undo it
    /// and the person pressing Install is not.
    /// </summary>
    /// <returns>
    /// Each app id, in any case, to the id the catalog will hold it under. The id column matches case
    /// exactly and the foreign key with it, so an edge is written to the app's own spelling of its id.
    /// </returns>
    private IReadOnlyDictionary<string, string> EnsurePrerequisites(IReadOnlyList<CatalogEntry> incoming)
    {
        // Nothing incoming needs anything, so no id can be missing, and taking edges away never
        // closes a loop.
        if (incoming.All(entry => Needs(entry).Count == 0))
        {
            return new Dictionary<string, string>();
        }

        var byId = Entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in incoming)
        {
            byId[entry.Id.Trim()] = entry;
        }

        var edges = byId.ToDictionary(pair => pair.Key, pair => Needs(pair.Value), StringComparer.OrdinalIgnoreCase);
        foreach (var entry in incoming)
        {
            var needs = Needs(entry);
            foreach (var id in needs.Where(id => !byId.ContainsKey(id)))
            {
                throw new PrerequisiteException($"'{id}' is not in the catalog, and {entry.Name.Trim()} needs it.");
            }

            PrerequisiteResolver.EnsureNoCycle(entry.Id.Trim(), needs, edges, byId);
        }

        return byId.ToDictionary(pair => pair.Key, pair => pair.Value.Id.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What an app needs first, blanks dropped and each id trimmed.</summary>
    private static IReadOnlyList<string> Needs(CatalogEntry entry)
        => [.. (entry.Requires ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim())];

    /// <summary>What an app needs first, each id spelled the way the app it names is stored.</summary>
    private static IReadOnlyList<string> Stored(CatalogEntry entry, IReadOnlyDictionary<string, string> ids)
        => [.. Needs(entry).Select(id => ids.GetValueOrDefault(id, id))];

    private static void WritePackages(SqliteConnection connection, SqliteTransaction transaction, CatalogEntry entry)
    {
        WritePackage(connection, transaction, entry.Id.Trim(), "action1",
            entry.HasAction1 ? JsonSerializer.Serialize(entry.Action1, Json) : null);
        WritePackage(connection, transaction, entry.Id.Trim(), "agent",
            entry.Agent is null ? null : JsonSerializer.Serialize(entry.Agent, Json));
    }

    private static void WritePackage(SqliteConnection connection, SqliteTransaction transaction, string id, string engine, string? definition)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = definition is null
            ? "DELETE FROM catalog_packages WHERE app_id = @id AND engine = @engine;"
            : """
                INSERT INTO catalog_packages (app_id, engine, definition_json) VALUES (@id, @engine, @definition)
                ON CONFLICT(app_id, engine) DO UPDATE SET definition_json = excluded.definition_json;
                """;
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@engine", engine);
        if (definition is not null)
        {
            command.Parameters.AddWithValue("@definition", definition);
        }

        command.ExecuteNonQuery();
    }

    /// <summary>The catalog in the shape of the checked-in `catalog.json`, so an export re-imports.</summary>
    public string ExportJson()
        => JsonSerializer.Serialize(new CatalogFile { Apps = [.. Entries] }, Export);

    /// <summary>The order the catalog file listed the apps in; every other order is by request.</summary>
    private static readonly SortColumns Sorts = new(
        "a.rowid",
        ("name", "a.name"),
        ("id", "a.id"),
        ("publisher", "a.publisher"),
        ("category", "a.category"));

    private static List<CatalogEntry> Read(SqliteConnection connection, string? id, bool visibleOnly = false, string? orderBy = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id, a.name, a.publisher, a.description, a.category, a.icon_url, a.featured, a.match_json, p.definition_json, a.hidden, agent.definition_json, a.engine_override, a.requirements, a.user_removable
            FROM catalog_apps a
            LEFT JOIN catalog_packages p ON p.app_id = a.id AND p.engine = 'action1'
            LEFT JOIN catalog_packages agent ON agent.app_id = a.id AND agent.engine = 'agent'
            """
            + (id is null
                ? (visibleOnly ? " WHERE a.hidden = 0" : "") + (orderBy ?? " ORDER BY a.rowid") + ";"
                : " WHERE a.id = @id COLLATE NOCASE;");
        if (id is not null)
        {
            command.Parameters.AddWithValue("@id", id);
        }

        var entries = new List<CatalogEntry>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new CatalogEntry
            {
                Id = reader.GetString(0),
                Name = reader.GetString(1),
                Publisher = reader.GetString(2),
                Description = reader.GetString(3),
                Category = reader.GetString(4),
                IconUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
                Featured = reader.GetInt64(6) != 0,
                Match = reader.IsDBNull(7) ? null : JsonSerializer.Deserialize<MatchRule>(reader.GetString(7), Json),
                Action1 = reader.IsDBNull(8) ? new Action1PackageRef() : JsonSerializer.Deserialize<Action1PackageRef>(reader.GetString(8), Json) ?? new Action1PackageRef(),
                Hidden = reader.GetInt64(9) != 0,
                Agent = reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<PackageDefinition>(reader.GetString(10), Json),
                EngineOverride = reader.IsDBNull(11) ? null : reader.GetString(11),
                Requirements = reader.IsDBNull(12) ? null : reader.GetString(12),
                UserRemovable = !reader.IsDBNull(13) && reader.GetInt64(13) != 0,
            });
        }

        AttachPrerequisites(connection, entries);
        return entries;
    }

    private static void AttachPrerequisites(SqliteConnection connection, List<CatalogEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }

        var byId = entries.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT app_id, requires_app_id FROM catalog_prerequisites ORDER BY app_id, position;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (byId.TryGetValue(reader.GetString(0), out var entry))
            {
                entry.Requires.Add(reader.GetString(1));
            }
        }
    }

    /// <summary>
    /// Writes what an app needs first. <see cref="EnsurePrerequisites"/> has refused a missing app or a
    /// loop before this runs.
    /// </summary>
    private void ReplacePrerequisites(SqliteConnection connection, SqliteTransaction transaction,
        string appId, IReadOnlyList<string> needs)
    {
        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM catalog_prerequisites WHERE app_id = @app;";
            clear.Parameters.AddWithValue("@app", appId);
            clear.ExecuteNonQuery();
        }

        var position = 0;
        foreach (var required in needs.Where(id => !string.IsNullOrWhiteSpace(id))
                     .Select(id => id.Trim())
                     .Where(id => !string.Equals(id, appId, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO catalog_prerequisites (app_id, requires_app_id, position) VALUES (@app, @requires, @position);";
            insert.Parameters.AddWithValue("@app", appId);
            insert.Parameters.AddWithValue("@requires", required);
            insert.Parameters.AddWithValue("@position", position++);
            insert.ExecuteNonQuery();
        }
    }
}
