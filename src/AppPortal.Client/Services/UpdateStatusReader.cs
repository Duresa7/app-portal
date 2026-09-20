using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Shared;

namespace AppPortal.Client.Services;

/// <summary>
/// The client's side of updating. It reads what the agent last wrote and can leave the agent a
/// request. It never downloads or installs anything itself, so nothing a signed-in user controls can
/// put a build on the machine: all the request says is that somebody would like the agent to look.
/// </summary>
public static class UpdateStatusReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string RunningVersion { get; } = ReadRunningVersion();

    public static string StateDirectory => Path.GetDirectoryName(ClientSettings.DefaultPath) ?? ".";

    public static string StatusPath => Path.Combine(StateDirectory, "update.json");

    /// <summary>The file the agent watches for, in the folder it made writable for exactly this.</summary>
    public static string RequestPath => Path.Combine(StateDirectory, "update.request");

    public static UpdateStatus? Read()
    {
        try
        {
            var path = StatusPath;
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateStatus>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Leaves update.request for the agent, which picks it up within ten seconds and does the work as
    /// SYSTEM. A request already waiting counts as asked: the folder only lets a user add a file, not
    /// rewrite one, and a second request would say nothing the first has not.
    /// </summary>
    public static bool RequestUpdate()
    {
        try
        {
            if (File.Exists(RequestPath))
            {
                return true;
            }

            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(RequestPath, DateTimeOffset.UtcNow.ToString("O"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ReadRunningVersion()
    {
        var informational = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return VersionText.TryParse(informational, out var version) ? VersionText.Short(version) : "0.0.0";
    }
}
