namespace AppPortal.Server.Options;

public sealed class Action1Options
{
    public const string Section = "Action1";

    /// <summary>"Live" talks to the Action1 cloud. "Fake" uses an in-memory stand-in for development and tests.</summary>
    public string Mode { get; set; } = "Live";

    /// <summary>Region base URL, for example https://app.action1.com/api/3.0 or https://app.na-2.action1.com/api/3.0.</summary>
    public string BaseUrl { get; set; } = "https://app.action1.com/api/3.0";

    public string OrgId { get; set; } = "";

    /// <summary>API credential Client ID. Supply through the environment, never through a committed file.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>API credential Client Secret. Supply through the environment, never through a committed file.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>How long Action1 keeps retrying a deployment on an offline endpoint, in minutes.</summary>
    public int RetryMinutes { get; set; } = 1440;

    public bool IsFake => string.Equals(Mode, "Fake", StringComparison.OrdinalIgnoreCase);
}
