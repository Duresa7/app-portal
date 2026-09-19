using AppPortal.Server.Devices;

namespace AppPortal.Server.Tests;

public sealed class DeviceStoreTests
{
    [Fact]
    public void Token_is_stored_hashed_and_authenticates_once_issued()
    {
        var path = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"), "devices.json");
        var store = new DeviceStore(path);

        var token = store.Add("PC1", "ep-1");

        Assert.StartsWith("apd_", token);
        Assert.DoesNotContain(token, File.ReadAllText(path));
        Assert.Contains(DeviceStore.Hash(token), File.ReadAllText(path));

        var device = store.Authenticate(token);
        Assert.NotNull(device);
        Assert.Equal("ep-1", device.EndpointId);
        Assert.Null(store.Authenticate(token + "x"));
        Assert.Null(store.Authenticate(""));

        Assert.True(store.Remove("PC1"));
        Assert.Null(store.Authenticate(token));
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void Adding_a_device_again_rotates_its_token()
    {
        var path = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"), "devices.json");
        var store = new DeviceStore(path);
        var first = store.Add("PC1", "ep-1");
        var second = store.Add("PC1", "ep-1");

        Assert.NotEqual(first, second);
        Assert.Null(store.Authenticate(first));
        Assert.NotNull(store.Authenticate(second));
        Assert.Single(store.All());
        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }
}
