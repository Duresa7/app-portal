using System;
using System.IO;
using System.Text.Json;

namespace AppPortal.Shared;

/// <summary>
/// Where the client finds its server and identifies its device. On Windows the file lives in
/// %ProgramData%\AppPortal\client.json, written by the installer; the signed-in user only needs read access.
/// Environment variables APPPORTAL_SERVER_URL and APPPORTAL_DEVICE_TOKEN override the file, which is handy in development.
/// </summary>
public class PortalSettings
{
    public string ServerUrl { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public int RefreshSeconds { get; set; } = 10;

    public string? UpdateRepository { get; set; }

    public bool IsConfigured => Uri.TryCreate(ServerUrl, UriKind.Absolute, out _) && !string.IsNullOrWhiteSpace(DeviceToken);

    public static string DefaultPath
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppPortal", "client.json");
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", "AppPortal", "client.json");
        }
    }

    /// <summary>
    /// The settings file this process really uses. APPPORTAL_CONFIG wins, so a developer run and a test
    /// keep their state in one directory instead of writing beside an installed agent.
    /// </summary>
    public static string ResolvedPath => Environment.GetEnvironmentVariable("APPPORTAL_CONFIG") ?? DefaultPath;

    public static PortalSettings Load(string? path = null)
    {
        path ??= ResolvedPath;
        var settings = new PortalSettings();
        if (File.Exists(path))
        {
            try
            {
                settings = JsonSerializer.Deserialize<PortalSettings>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? settings;
            }
            catch (JsonException)
            {
                // Fall through to environment overrides; the UI reports an unconfigured client.
            }
        }

        var url = Environment.GetEnvironmentVariable("APPPORTAL_SERVER_URL");
        var token = Environment.GetEnvironmentVariable("APPPORTAL_DEVICE_TOKEN");
        if (!string.IsNullOrWhiteSpace(url))
        {
            settings.ServerUrl = url;
        }

        if (!string.IsNullOrWhiteSpace(token))
        {
            settings.DeviceToken = token;
        }

        return settings;
    }
}
