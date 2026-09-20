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

/// <summary>
/// The catalog, held in the database. `catalog.json` seeds an empty database once and is the format
/// `catalog import` and `catalog export` read and write; after that the database is what the API serves.
/// </summary>
public sealed class CatalogStore
{
    private const string ActionOneEngine = "action1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
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

    /// <summary>
    /// Upserts by id. Apps the file does not mention are left alone, but every field of the ones it does
    /// mention comes from the file, <c>hidden</c> included: an export is meant to restore what it captured,
    /// so a file that omits the flag says the app is visible. Seeding only runs on an empty catalog, so a
    /// restart never walks over an administrator's decision; an upload is a deliberate act.
    /// </summary>
    public int Import(IReadOnlyList<CatalogEntry> entries)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            var now = SqlTime.Now();
            using (var app = connection.CreateCommand())
            {
                app.Transaction = transaction;
                app.CommandText = """
                    INSERT INTO catalog_apps (id, name, publisher, description, category, icon_url, featured, hidden, match_json, engine_override, created_at, updated_at)
                    VALUES (@id, @name, @publisher, @description, @category, @icon, @featured, @hidden, @match, NULL, @now, @now)
                    ON CONFLICT(id) DO UPDATE SET
                        name = excluded.name, publisher = excluded.publisher, description = excluded.description,
                        category = excluded.category, icon_url = excluded.icon_url, featured = excluded.featured,
                        hidden = excluded.hidden, match_json = excluded.match_json, updated_at = excluded.updated_at;
                    """;
                app.Parameters.AddWithValue("@id", entry.Id);
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

            using var package = connection.CreateCommand();
            package.Transaction = transaction;
            package.CommandText = """
                INSERT INTO catalog_packages (app_id, engine, definition_json) VALUES (@id, @engine, @definition)
                ON CONFLICT(app_id, engine) DO UPDATE SET definition_json = excluded.definition_json;
                """;
            package.Parameters.AddWithValue("@id", entry.Id);
            package.Parameters.AddWithValue("@engine", ActionOneEngine);
            package.Parameters.AddWithValue("@definition", JsonSerializer.Serialize(entry.Action1, Json));
            package.ExecuteNonQuery();
        }

        transaction.Commit();
        return entries.Count;
    }

    /// <summary>
    /// Writes one app and its Action1 package. The edit pages own every field including hidden, unlike
    /// <see cref="Import"/>, which leaves an app's hidden flag where the administrator put it.
    /// </summary>
    public void Upsert(CatalogEntry entry)
    {
        Validate(entry);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        var now = SqlTime.Now();

        using (var app = connection.CreateCommand())
        {
            app.Transaction = transaction;
            app.CommandText = """
                INSERT INTO catalog_apps (id, name, publisher, description, category, icon_url, featured, hidden, match_json, engine_override, created_at, updated_at)
                VALUES (@id, @name, @publisher, @description, @category, @icon, @featured, @hidden, @match, NULL, @now, @now)
                ON CONFLICT(id) DO UPDATE SET
                    name = excluded.name, publisher = excluded.publisher, description = excluded.description,
                    category = excluded.category, icon_url = excluded.icon_url, featured = excluded.featured,
                    hidden = excluded.hidden, match_json = excluded.match_json, updated_at = excluded.updated_at;
                """;
            app.Parameters.AddWithValue("@id", entry.Id.Trim());
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

        using (var package = connection.CreateCommand())
        {
            package.Transaction = transaction;
            package.CommandText = """
                INSERT INTO catalog_packages (app_id, engine, definition_json) VALUES (@id, @engine, @definition)
                ON CONFLICT(app_id, engine) DO UPDATE SET definition_json = excluded.definition_json;
                """;
            package.Parameters.AddWithValue("@id", entry.Id.Trim());
            package.Parameters.AddWithValue("@engine", ActionOneEngine);
            package.Parameters.AddWithValue("@definition", JsonSerializer.Serialize(entry.Action1, Json));
            package.ExecuteNonQuery();
        }

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

    /// <summary>The same rules <see cref="Parse"/> applies to a file, for one app coming off a form.</summary>
    private static void Validate(CatalogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Id))
        {
            throw new InvalidDataException("An app needs an id.");
        }

        if (string.IsNullOrWhiteSpace(entry.Name))
        {
            throw new InvalidDataException("An app needs a name.");
        }

        if (string.IsNullOrWhiteSpace(entry.Action1.PackageId))
        {
            throw new InvalidDataException("An app needs an Action1 package id.");
        }
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
            SELECT a.id, a.name, a.publisher, a.description, a.category, a.icon_url, a.featured, a.match_json, p.definition_json, a.hidden
            FROM catalog_apps a
            LEFT JOIN catalog_packages p ON p.app_id = a.id AND p.engine = 'action1'
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
            });
        }

        return entries;
    }
}
