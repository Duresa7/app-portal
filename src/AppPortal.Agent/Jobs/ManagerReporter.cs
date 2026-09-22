using System.Net.Http.Headers;
using System.Net.Http.Json;

using AppPortal.Agent.Executors;
using AppPortal.Agent.Sessions;
using AppPortal.Shared;

namespace AppPortal.Agent.Jobs;

/// <summary>
/// Tells the server which package managers this PC has. Without it an administrator adds an npm app
/// and learns from a failure on every device how many of them lacked Node.js; with it the catalog page
/// says so before anything is saved.
/// </summary>
public sealed class ManagerReporter(
    HttpClient http,
    IProcessRunner processes,
    IPackageManagerLocator locator,
    ILogger<ManagerReporter> logger,
    IUserSessionLauncher? sessions = null,
    Func<PortalSettings>? settings = null,
    TimeSpan? interval = null,
    SoftwareReporter? software = null) : BackgroundService
{
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _interval = interval ?? TimeSpan.FromDays(1);
    private readonly Func<PortalSettings> _settings = settings ?? (() => PortalSettings.Load());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // A PC that has not enrolled yet has nowhere to report to. It looks again soon rather than
            // a day later, because enrollment usually finishes within a minute of the service starting.
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

    /// <summary>Finds and reports once. True when the server heard it.</summary>
    public async Task<bool> ReportAsync(CancellationToken ct)
    {
        try
        {
            var current = _settings();
            if (!current.IsConfigured)
            {
                return false;
            }

            var found = await DetectAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(new Uri(current.ServerUrl.TrimEnd('/') + "/"), "api/v1/agent/managers"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.DeviceToken);
            request.Content = JsonContent.Create(found);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("The server refused the package manager report ({Status})", (int)response.StatusCode);
                return false;
            }

            logger.LogInformation("Reported {Count} package managers", found.Count);
            if (software is not null)
            {
                // What each of them installed, on the same schedule. Nothing else refreshes it for
                // software somebody installed through a manager outside the portal.
                await software.ReportManagersAsync(current, ct);
                foreach (var account in SignedIn())
                {
                    await software.ReportManagersAsync(current, ct, account);
                }
            }

            return true;
        }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or InvalidOperationException
                                        or UnauthorizedAccessException or TaskCanceledException)
                                   && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Could not report package managers ({Reason})", ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    /// Every manager this PC has, machine-wide first. For a manager that can live in a profile, each
    /// signed-in person's copy too, when it is a different file from the machine's: Scoop, Cargo and
    /// Bun are usually only there, and a report that looked for them as SYSTEM alone would say no PC
    /// has them.
    /// </summary>
    public async Task<IReadOnlyList<DeviceManager>> DetectAsync(CancellationToken ct)
    {
        var accounts = SignedIn();
        var found = new List<DeviceManager>();
        foreach (var manager in PackageManagers.All)
        {
            var machine = locator.Find(manager, null);
            if (machine is not null && await VersionAsync(machine, manager, ct) is { } version)
            {
                found.Add(new DeviceManager(manager.Name, version));
            }

            if (!manager.Scopes.Contains("user"))
            {
                continue;
            }

            foreach (var account in accounts)
            {
                // Found by path only. Asking for the version would mean starting a process in that
                // person's session once a day for every manager, to fill in a column.
                var theirs = locator.Find(manager, account);
                if (theirs is not null && !string.Equals(theirs, machine, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(new DeviceManager(manager.Name, "", account));
                }
            }
        }

        return found;
    }

    /// <summary>
    /// The first line the manager prints about itself, or null when it would not say. A manager whose
    /// executable is on disk but will not run is not a manager this PC can install through.
    /// </summary>
    private async Task<string?> VersionAsync(string executable, PackageManagerDescriptor manager, CancellationToken ct)
    {
        try
        {
            var result = await processes.RunAsync(executable, manager.VersionArguments, null, VersionTimeout, ct);
            if (result.ExitCode != 0)
            {
                return null;
            }

            var first = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "";
            return first.Length <= 64 ? first : first[..64];
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or InvalidOperationException
                                       or System.ComponentModel.Win32Exception)
        {
            logger.LogInformation("{Manager} is on disk but did not answer ({Reason})", manager.Name, ex.GetType().Name);
            return null;
        }
    }

    private IReadOnlyList<string> SignedIn()
    {
        try
        {
            return sessions?.SignedInAccounts() ?? [];
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not read the signed-in accounts ({Reason})", ex.GetType().Name);
            return [];
        }
    }
}
