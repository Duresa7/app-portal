using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.ServiceProcess;

using AppPortal.Shared;

namespace AppPortal.Agent.Jobs;

/// <summary>One service or driver as the service manager lists it.</summary>
public sealed record ServiceEntry(string Name, string Type, string State, string StartType);

/// <summary>
/// Every service and driver on the PC. Behind an interface because the answer comes from the service
/// manager, and which of them count as anti-cheat is a rule that has to be testable without one.
/// </summary>
public interface IServiceInventory
{
    IReadOnlyList<ServiceEntry> List();
}

/// <summary>What the agent uses away from Windows: there are no services to list.</summary>
public sealed class NoServiceInventory : IServiceInventory
{
    public IReadOnlyList<ServiceEntry> List() => [];
}

[SupportedOSPlatform("windows")]
public sealed class WindowsServiceInventory : IServiceInventory
{
    public IReadOnlyList<ServiceEntry> List()
    {
        var found = new List<ServiceEntry>();
        Add(found, ServiceController.GetServices(), "service");
        // Drivers are a list of their own. Vanguard's vgk and BattlEye's BEDaisy are drivers, and they
        // are the half of each product that decides whether a game will start.
        Add(found, ServiceController.GetDevices(), "driver");
        return found;
    }

    private static void Add(List<ServiceEntry> found, ServiceController[] controllers, string type)
    {
        foreach (var controller in controllers)
        {
            using (controller)
            {
                try
                {
                    found.Add(new ServiceEntry(controller.ServiceName, type, State(controller.Status), Start(controller.StartType)));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Removed between the listing and the read, or not readable by anyone. Either way it
                    // has nothing to say.
                }
            }
        }
    }

    private static string State(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => "running",
        ServiceControllerStatus.Stopped => "stopped",
        ServiceControllerStatus.Paused => "paused",
        ServiceControllerStatus.StartPending => "starting",
        ServiceControllerStatus.StopPending => "stopping",
        ServiceControllerStatus.ContinuePending => "continuing",
        ServiceControllerStatus.PausePending => "pausing",
        _ => "unknown",
    };

    private static string Start(ServiceStartMode mode) => mode switch
    {
        ServiceStartMode.Automatic => "automatic",
        ServiceStartMode.Manual => "manual",
        ServiceStartMode.Disabled => "disabled",
        ServiceStartMode.Boot => "boot",
        ServiceStartMode.System => "system",
        _ => "unknown",
    };
}

/// <summary>
/// Tells the server which kernel anti-cheat this PC carries and what state each piece is in, on start
/// and once a day. An administrator asked why a game will not start can then read "Riot Vanguard,
/// stopped" on the device page instead of asking for a remote session. The whole list is sent every
/// time, an empty one included, so a product somebody removed leaves the record.
/// </summary>
public sealed class AntiCheatReporter(
    HttpClient http,
    IServiceInventory services,
    ILogger<AntiCheatReporter> logger,
    Func<PortalSettings>? settings = null,
    TimeSpan? interval = null) : BackgroundService
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromDays(1);
    private readonly Func<PortalSettings> _settings = settings ?? (() => PortalSettings.Load());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Not enrolled yet: look again soon, because enrollment usually finishes within a minute
            // of the service starting.
            var delay = await ReportAsync(stoppingToken) ? _interval : TimeSpan.FromMinutes(5);
            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>The known anti-cheat among these services, each once.</summary>
    public static IReadOnlyList<DeviceAntiCheat> Find(IEnumerable<ServiceEntry> entries)
        => [.. entries
            .Select(entry => (Entry: entry, Product: AntiCheats.Find(entry.Name)))
            .Where(found => found.Product is not null)
            .DistinctBy(found => found.Entry.Name.ToUpperInvariant())
            .OrderBy(found => found.Product!.Name, StringComparer.Ordinal)
            .ThenBy(found => found.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Select(found => new DeviceAntiCheat(found.Product!.Name, found.Entry.Name, found.Entry.Type, found.Entry.State,
                found.Entry.StartType))];

    /// <summary>Lists and reports once. True when the server heard it.</summary>
    public async Task<bool> ReportAsync(CancellationToken ct)
    {
        try
        {
            var current = _settings();
            if (!current.IsConfigured)
            {
                return false;
            }

            var found = Find(services.List());
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(new Uri(current.ServerUrl.TrimEnd('/') + "/"), "api/v1/agent/anticheat"));
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", current.DeviceToken);
            request.Content = JsonContent.Create(found);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The server refused the anti-cheat report ({Status})", (int)response.StatusCode);
                return false;
            }

            logger.LogInformation("Reported {Count} anti-cheat services and drivers", found.Count);
            return true;
        }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or InvalidOperationException
                                        or UnauthorizedAccessException or TaskCanceledException
                                        or System.ComponentModel.Win32Exception)
                                   && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not report anti-cheat ({Reason})", ex.GetType().Name);
            return false;
        }
    }
}
