using System.Globalization;
using System.Reflection;

using AppPortal.Server.Options;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace AppPortal.Server.Data;

/// <summary>
/// The one SQLite file under the data directory, and the numbered migrations that shape it.
/// Every store opens its own short-lived connection; SQLite in WAL mode lets readers run while one
/// writer works, and <c>busy_timeout</c> makes a concurrent writer wait rather than fail.
/// </summary>
public sealed class Database
{
    public const string FileName = "app-portal.db";

    private readonly string _connectionString;
    private readonly ILogger<Database>? _logger;

    public Database(IOptions<PortalOptions> options, IHostEnvironment env, ILogger<Database> logger)
        : this(System.IO.Path.Combine(Resolve(options.Value.DataDirectory, env.ContentRootPath), FileName), logger)
    {
    }

    public Database(string path, ILogger<Database>? logger = null)
    {
        Path = path;
        _logger = logger;
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = ConnectionStringFor(path);
    }

    public string Path { get; }

    private static string ConnectionStringFor(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        // Private cache, which is the default: a shared cache serialises connections at table level
        // and answers a second writer with an error that busy_timeout does not wait out.
        Cache = SqliteCacheMode.Private,
        Pooling = true,
    }.ToString();

    /// <summary>
    /// Closes the pooled connections to this one file, so that it can be deleted or read as bytes while
    /// the process keeps running. Deliberately narrow: <c>SqliteConnection.ClearAllPools</c> reaches
    /// every pool in the process and disposes handles that other work is holding.
    /// </summary>
    public void ClearPool() => ClearPoolAt(_connectionString);

    /// <summary>The same, for a file this process has not opened through a <see cref="Database"/>.</summary>
    public static void ClearPoolFor(string path) => ClearPoolAt(ConnectionStringFor(path));

    private static void ClearPoolAt(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        // WAL is a property of the file and survives; the other two are per connection.
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 10000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    /// <summary>Applies every migration this build carries that the file has not seen. Safe to run again.</summary>
    public void Migrate()
    {
        using var connection = Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);";
            create.ExecuteNonQuery();
        }

        var applied = new HashSet<int>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT version FROM schema_version;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                applied.Add(reader.GetInt32(0));
            }
        }

        foreach (var (version, name, sql) in Migrations())
        {
            if (!applied.Add(version))
            {
                continue;
            }

            using var transaction = connection.BeginTransaction();
            using (var run = connection.CreateCommand())
            {
                run.Transaction = transaction;
                run.CommandText = sql;
                run.ExecuteNonQuery();
            }

            using (var record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_version (version, applied_at) VALUES (@v, @at);";
                record.Parameters.AddWithValue("@v", version);
                record.Parameters.AddWithValue("@at", SqlTime.Now());
                record.ExecuteNonQuery();
            }

            transaction.Commit();
            _logger?.LogInformation("Applied migration {Version} ({Name})", version, name);
        }
    }

    private static IEnumerable<(int Version, string Name, string Sql)> Migrations()
    {
        var assembly = typeof(Database).Assembly;
        var prefix = typeof(Database).Namespace + ".Migrations.";
        var found = new List<(int, string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var name = resource[prefix.Length..];
            var dash = name.IndexOf('-');
            if (dash < 0 || !int.TryParse(name[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException($"Migration '{name}' is not named <number>-<slug>.sql.");
            }

            using var stream = assembly.GetManifestResourceStream(resource)
                               ?? throw new InvalidOperationException($"Migration '{name}' could not be read.");
            using var reader = new StreamReader(stream);
            found.Add((version, name, reader.ReadToEnd()));
        }

        return found.OrderBy(m => m.Item1);
    }

    private static string Resolve(string path, string contentRoot)
        => System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(contentRoot, path);
}

/// <summary>Timestamps are ISO 8601 in UTC, so they sort as text and read the same in every tool.</summary>
public static class SqlTime
{
    public static string Now() => From(DateTimeOffset.UtcNow);

    public static string From(DateTimeOffset value)
        => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static string? FromOptional(DateTimeOffset? value) => value is null ? null : From(value.Value);

    public static DateTimeOffset Parse(string text)
        => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static DateTimeOffset? ParseOptional(string? text)
        => string.IsNullOrEmpty(text) ? null : Parse(text);
}
