using AppPortal.Shared;

namespace AppPortal.Client.Services;

public sealed class ClientSettings : PortalSettings
{
    public new static ClientSettings Load(string? path = null)
    {
        var settings = PortalSettings.Load(path);
        return new ClientSettings
        {
            ServerUrl = settings.ServerUrl,
            DeviceToken = settings.DeviceToken,
            RefreshSeconds = settings.RefreshSeconds,
            UpdateRepository = settings.UpdateRepository,
        };
    }
}
