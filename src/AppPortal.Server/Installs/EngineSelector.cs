using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Shared;

namespace AppPortal.Server.Installs;

/// <summary>
/// Which engine carries out an install when more than one could. The rule is deliberately a ladder
/// rather than a negotiation, so that an administrator can always answer "why did it go that way" by
/// reading four lines in order.
/// </summary>
public static class EngineSelector
{
    /// <summary>
    /// The engine that would run this app on this device, or null when none can. In order: what the
    /// device was told to prefer, then what the app was told to prefer, then the server's default,
    /// then whichever single engine is left. A preference that is not available is passed over rather
    /// than obeyed: it says which to choose between, not which to fail on.
    /// </summary>
    public static string? Choose(DeviceRecord device, CatalogEntry app, string? serverDefault)
    {
        var action1 = app.HasAction1 && !string.IsNullOrWhiteSpace(device.EndpointId);
        var agent = app.Agent is not null && device.HasAgent;
        if (!action1 && !agent)
        {
            return null;
        }

        if (!action1 || !agent)
        {
            // Only one can run it, so nobody's preference is interesting.
            return action1 ? EngineLabel.Action1 : EngineLabel.Agent;
        }

        foreach (var preference in new[] { device.EnginePreference, app.EngineOverride, serverDefault })
        {
            if (Available(preference, action1, agent))
            {
                return preference!.ToLowerInvariant();
            }
        }

        // Both are available and nothing chose. Action1 is the engine that existed first, and a device
        // that has both was managed by it before the agent arrived.
        return EngineLabel.Action1;
    }

    private static bool Available(string? engine, bool action1, bool agent) => engine?.ToLowerInvariant() switch
    {
        EngineLabel.Action1 => action1,
        EngineLabel.Agent => agent,
        _ => false,
    };
}
