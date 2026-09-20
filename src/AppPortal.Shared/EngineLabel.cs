namespace AppPortal.Shared;

/// <summary>
/// How an install was carried out, said the same way everywhere. The server's history pages use it
/// now; the client shows it beside an app from M3-05, once there is more than one engine to tell apart.
/// </summary>
public static class EngineLabel
{
    public const string Action1 = "action1";
    public const string Agent = "agent";

    public static string For(string? engine) => engine?.ToLowerInvariant() switch
    {
        Action1 => "via Action1",
        Agent => "via Agent",
        null or "" => "",
        _ => "via " + engine,
    };
}
