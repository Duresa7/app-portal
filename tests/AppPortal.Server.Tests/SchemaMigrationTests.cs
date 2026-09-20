using System.Globalization;

using AppPortal.Server.Data;

namespace AppPortal.Server.Tests;

/// <summary>
/// Every deployment of this server is a database that stopped at some earlier release and is asked to
/// carry on. A migration that only works on an empty file, or that leaves a column out on one path and
/// not the other, is found here rather than by the first company that upgrades.
/// </summary>
public sealed class SchemaMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));

    public SchemaMigrationTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Migrating_a_migrated_database_changes_nothing()
    {
        var database = Open("twice");
        database.Migrate();
        var first = Shape(database);

        database.Migrate();

        Assert.Equal(first, Shape(database));
    }

    /// <summary>
    /// A database stopped at any earlier release ends up with the schema a fresh one has. Every
    /// starting point, because the one that is skipped is the one somebody is running.
    /// </summary>
    [Fact]
    public void Every_upgrade_path_ends_at_the_same_schema()
    {
        var fresh = Open("fresh");
        fresh.Migrate();
        var expected = Shape(fresh);

        foreach (var stoppedAt in Versions())
        {
            var database = Open($"from-{stoppedAt}");
            ApplyUpTo(database, stoppedAt);

            database.Migrate();

            var actual = Shape(database);
            Assert.True(expected == actual,
                $"A database that stopped at migration {stoppedAt} ends up with a different schema.\n{FirstDifference(expected, actual)}");
        }
    }

    private static string FirstDifference(string expected, string actual)
    {
        var want = expected.Split('\n');
        var got = actual.Split('\n');
        for (var i = 0; i < Math.Max(want.Length, got.Length); i++)
        {
            var left = i < want.Length ? want[i] : "(nothing)";
            var right = i < got.Length ? got[i] : "(nothing)";
            if (left != right)
            {
                return $"fresh:   {left}\nupgrade: {right}";
            }
        }

        return "";
    }

    /// <summary>
    /// Migration 013 rebuilds <c>device_software</c> to widen its primary key, which SQLite cannot
    /// alter in place. What it holds is a cache of what the agent last reported, so it is allowed to
    /// start empty; what it is not allowed to do is take anything else with it.
    /// </summary>
    [Fact]
    public void Rebuilding_the_software_table_leaves_the_rest_of_the_database_alone()
    {
        var database = Open("rebuild");
        ApplyUpTo(database, 12);
        using (var connection = database.Open())
        using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO devices (id, name, token_hash, created_at) VALUES ('d1', 'PC', 'hash', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO catalog_apps (id, name, created_at, updated_at) VALUES ('app', 'An App', '', '');
                INSERT INTO installs (id, device_id, app_id, app_name, state, requested_at, last_checked_at)
                VALUES ('i1', 'd1', 'app', 'An App', 'Succeeded', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO device_software (device_id, name, version, seen_at) VALUES ('d1', 'An App', '1.0', '2026-01-01T00:00:00.0000000Z');
                """;
            seed.ExecuteNonQuery();
        }

        database.Migrate();

        Assert.Equal(1, Count(database, "devices"));
        Assert.Equal(1, Count(database, "catalog_apps"));
        Assert.Equal(1, Count(database, "installs"));
        // The cache alone, and it refills from the device rather than from here.
        Assert.Equal(0, Count(database, "device_software"));
    }

    private Database Open(string name) => new(Path.Combine(_root, name + ".db"));

    private static IEnumerable<int> Versions()
        => Resources().Select(resource => VersionOf(resource.Name)).Order();

    /// <summary>Puts the database in the state a release that carried these migrations left it in.</summary>
    private static void ApplyUpTo(Database database, int version)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER PRIMARY KEY, applied_at TEXT NOT NULL);";
        command.ExecuteNonQuery();
        foreach (var (name, sql) in Resources().OrderBy(r => VersionOf(r.Name)))
        {
            var applied = VersionOf(name);
            if (applied > version)
            {
                break;
            }

            command.CommandText = sql;
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO schema_version (version, applied_at) VALUES (@version, @at);";
            command.Parameters.Clear();
            command.Parameters.AddWithValue("@version", applied);
            command.Parameters.AddWithValue("@at", SqlTime.Now());
            command.ExecuteNonQuery();
            command.Parameters.Clear();
        }
    }

    /// <summary>Every table, index and trigger with the statement that made it, plus what has run.</summary>
    private static string Shape(Database database)
    {
        using var connection = database.Open();
        var lines = new List<string>();
        using (var objects = connection.CreateCommand())
        {
            objects.CommandText = "SELECT type, name, COALESCE(sql, '') FROM sqlite_master ORDER BY type, name;";
            using var reader = objects.ExecuteReader();
            while (reader.Read())
            {
                lines.Add($"{reader.GetString(0)} {reader.GetString(1)}\n{Normalise(reader.GetString(2))}");
            }
        }

        using (var versions = connection.CreateCommand())
        {
            versions.CommandText = "SELECT version FROM schema_version ORDER BY version;";
            using var reader = versions.ExecuteReader();
            while (reader.Read())
            {
                lines.Add($"applied {reader.GetInt32(0)}");
            }
        }

        return string.Join("\n---\n", lines);
    }

    private static int Count(Database database, string table)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Line endings and indentation are not schema; a difference in them is not a difference.</summary>
    private static string Normalise(string sql)
        => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int VersionOf(string name)
        => int.Parse(name.Split('-')[0], NumberStyles.None, CultureInfo.InvariantCulture);

    private static IEnumerable<(string Name, string Sql)> Resources()
    {
        var assembly = typeof(Database).Assembly;
        const string Marker = ".Migrations.";
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.Contains(Marker, StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            yield return (resource.Split(Marker)[1], reader.ReadToEnd());
        }
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*.db"))
        {
            Database.ClearPoolFor(file);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
