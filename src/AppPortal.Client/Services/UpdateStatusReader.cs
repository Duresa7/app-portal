using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AppPortal.Shared;

namespace AppPortal.Client.Services;

/// <summary>
/// The client's side of updating. It reads what the updater last wrote and can ask the updater's
/// scheduled task to run. It never downloads anything itself, so nothing a signed-in user controls
/// can put a build on the machine.
/// </summary>
public static class UpdateStatusReader
{
    public const string TaskName = "App Portal Updater";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string RunningVersion { get; } = ReadRunningVersion();

    public static string StatusPath => Path.Combine(Path.GetDirectoryName(ClientSettings.DefaultPath) ?? ".", "update.json");

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
    /// Starts the updater's scheduled task. Its security descriptor lets any signed-in user run it and
    /// nothing more; the task itself runs as SYSTEM, which is the only account that can write to Program Files.
    /// </summary>
    public static bool RequestUpdate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Run /TN \"{TaskName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
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
