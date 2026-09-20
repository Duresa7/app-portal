namespace AppPortal.Shared;

/// <summary>
/// Whether an install is waiting for a restart. Said the same way in the database, the JSON and on
/// screen, where it is always "restart" and never "reboot": one of those is a word people use.
/// </summary>
/// <summary>
/// Whether a row is software going on or coming off. One word in the database, the JSON and on
/// screen, because the history shows both in the same list.
/// </summary>
public static class InstallKind
{
    public const string Install = "install";
    public const string Uninstall = "uninstall";
}

public static class RebootState
{
    public const string Pending = "pending";
    public const string Confirmed = "confirmed";

    public const string WaitingDetail = "Restart to finish";
}

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
