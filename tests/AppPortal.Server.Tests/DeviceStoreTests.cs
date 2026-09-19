using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

public sealed class DeviceStoreTests
{
    [Fact]
    public void Token_is_stored_hashed_and_authenticates_once_issued()
    {
        using var test = new TestDatabase();
        var store = new DeviceStore(test.Database);

        var token = store.Add("PC1", "ep-1");

        Assert.StartsWith("apd_", token);
        var stored = store.All().Single();
        Assert.Equal(DeviceStore.Hash(token), stored.TokenSha256);
        Assert.NotEqual(token, stored.TokenSha256);
        Assert.NotEmpty(stored.Id);

        var device = store.Authenticate(token);
        Assert.NotNull(device);
        Assert.Equal("ep-1", device.EndpointId);
        Assert.Null(store.Authenticate(token + "x"));
        Assert.Null(store.Authenticate(""));

        Assert.True(store.Remove("PC1"));
        Assert.Null(store.Authenticate(token));
        Assert.False(store.Remove("PC1"));
    }

    [Fact]
    public void The_plaintext_token_is_nowhere_in_the_database_file()
    {
        using var test = new TestDatabase();
        var token = new DeviceStore(test.Database).Add("PC1", "ep-1");

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = File.ReadAllBytes(test.Database.Path);
        var text = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.Contains(DeviceStore.Hash(token), text, StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_a_device_again_rotates_its_token_and_keeps_one_row()
    {
        using var test = new TestDatabase();
        var store = new DeviceStore(test.Database);
        var first = store.Add("PC1", "ep-1");
        var second = store.Add("pc1", "ep-1");

        Assert.NotEqual(first, second);
        Assert.Null(store.Authenticate(first));
        Assert.NotNull(store.Authenticate(second));
        Assert.Single(store.All());
    }

    [Fact]
    public void A_device_with_install_history_cannot_be_removed()
    {
        using var test = new TestDatabase();
        var store = new DeviceStore(test.Database);
        store.Add("PC1", "ep-1");
        new InstallStore(test.Database).Upsert(new InstallRecord
        {
            Id = "i1",
            DeviceName = "PC1",
            AppId = "chrome",
            AppName = "Google Chrome",
            RequestedAt = DateTimeOffset.UtcNow,
            State = InstallState.Succeeded,
        });

        var ex = Assert.Throws<DeviceInUseException>(() => store.Remove("PC1"));
        Assert.Contains("install", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(store.All());
    }
}
