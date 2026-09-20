using AppPortal.Server.Data;

namespace AppPortal.Server.Tests;

/// <summary>
/// Test classes run beside each other, so cleaning up after one must not reach into another. This is
/// the rule that keeps that true, written down as a test because breaking it fails somewhere else
/// entirely: a connection another class was in the middle of using, with no hint of who closed it.
/// </summary>
public sealed class DatabasePoolTests
{
    [Fact]
    public void Disposing_one_test_database_leaves_another_one_working()
    {
        using var other = new TestDatabase();
        using var held = other.Database.Open();

        using (new TestDatabase())
        {
            // Nothing; the point is what its Dispose does on the way out.
        }

        using var command = held.CreateCommand();
        command.CommandText = "SELECT count(*) FROM devices;";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void Clearing_a_pool_does_not_stop_the_database_being_opened_again()
    {
        using var test = new TestDatabase();
        test.Database.ClearPool();

        using var connection = test.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM schema_version;";
        Assert.True((long)command.ExecuteScalar()! > 0);
    }

    [Fact]
    public void A_file_can_be_read_as_bytes_once_its_own_pool_is_cleared()
    {
        var root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Database.FileName);
        try
        {
            new Database(path).Migrate();
            Database.ClearPoolFor(path);

            Assert.NotEmpty(File.ReadAllBytes(path));
            Directory.Delete(root, recursive: true);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            Database.ClearPoolFor(path);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
        }
    }
}
