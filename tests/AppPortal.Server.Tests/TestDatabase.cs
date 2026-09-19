using AppPortal.Server.Data;

namespace AppPortal.Server.Tests;

/// <summary>A migrated database in a temporary folder, and the settings a test server needs to find it.</summary>
public sealed class TestDatabase : IDisposable
{
    public TestDatabase(string? root = null)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(Root, "data");
        Directory.CreateDirectory(DataDirectory);
        Database = new Database(Path.Combine(DataDirectory, AppPortal.Server.Data.Database.FileName));
        Database.Migrate();
    }

    public string Root { get; }

    public string DataDirectory { get; }

    public Database Database { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
