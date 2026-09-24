using System.Net.Http.Headers;
using System.Net.Http.Json;

using AppPortal.Agent.Executors;
using AppPortal.Agent.Sessions;
using AppPortal.Shared;

namespace AppPortal.Agent.Jobs;

/// <summary>
/// Tells the server what is on this device after an install succeeds. Without it a device whose only
/// engine is the agent shows an empty Installed list, because the portal's other source of inventory
/// is the Action1 endpoint such a device does not have.
/// </summary>
public sealed class SoftwareReporter(
    HttpClient http,
    IProcessRunner processes,
    ILogger<SoftwareReporter> logger,
    IUserSessionLauncher? sessions = null,
    WingetLocator? locator = null,
    IPackageManagerLocator? managers = null)
{
    private const string ListArguments = "list --accept-source-agreements --disable-interactivity";

    private static readonly TimeSpan ListTimeout = TimeSpan.FromMinutes(5);

    private readonly WingetLocator _locator = locator ?? new WingetLocator();

    /// <summary>
    /// What this device carries. With an account, what that person's own profile carries: the sweep
    /// runs inside their session, because software installed into a profile is invisible from outside
    /// it, which is the same reason the install had to run there in the first place.
    /// </summary>
    public async Task ReportAsync(PortalSettings settings, CancellationToken ct, string? account = null)
    {
        try
        {
            var executable = _locator.Find();
            if (executable is null || !settings.IsConfigured)
            {
                return;
            }

            var dependencies = _locator.Dependencies(executable);
            ProcessResult? result;
            if (account is null)
            {
                result = await processes.RunAsync(executable, ListArguments, dependencies, null, ListTimeout, ct);
            }
            else if (sessions is null)
            {
                return;
            }
            else
            {
                result = await sessions.RunAsAsync(account, executable, ListArguments, dependencies, null, ListTimeout, ct);
                if (result is null)
                {
                    // They signed out between the install and the sweep. Their list keeps what it had.
                    return;
                }
            }

            var software = WingetList.Parse(result.Output);
            if (software.Count == 0)
            {
                // Sending nothing would clear the record. An unreadable table is not an empty device.
                // The exit code and the first thing winget said are what tell a missing DLL from a
                // prompt or a table this parser does not know; without them the line is undiagnosable.
                logger.LogWarning("Could not read the installed software list (exit code {ExitCode}, output '{Output}'); leaving the last report alone",
                    result.ExitCode, FirstLine(result.Output));
                return;
            }

            await PostAsync(settings, software, account, source: null, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The install worked. Failing to describe it afterwards must not turn that into a failure.
            logger.LogWarning("Could not report installed software ({Reason}: {Message})", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>The first line with anything on it, cut short enough for one log line.</summary>
    internal static string FirstLine(string output)
    {
        var line = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return line.Length <= 200 ? line : line[..200];
    }

    /// <summary>
    /// What each package manager on this PC installed, one report per manager. Machine-wide lists the
    /// managers that can install for everyone; with an account, the managers that can install into a
    /// profile, listed inside that person's session, which is where their packages are.
    /// </summary>
    public async Task ReportManagersAsync(PortalSettings settings, CancellationToken ct, string? account = null)
    {
        var scope = account is null ? "machine" : "user";
        foreach (var manager in PackageManagers.All.Where(m => m.Scopes.Contains(scope)))
        {
            await ReportManagerAsync(settings, manager, ct, account);
        }
    }

    /// <summary>
    /// What one manager installed. Called straight after an install through it, so the card turns to
    /// Installed without waiting for the daily sweep.
    /// </summary>
    public async Task ReportManagerAsync(PortalSettings settings, PackageManagerDescriptor manager, CancellationToken ct,
        string? account = null)
    {
        try
        {
            if (managers is null || !settings.IsConfigured || managers.Find(manager, account) is not { } executable)
            {
                return;
            }

            var result = account is null
                ? await processes.RunAsync(executable, manager.List, null, ListTimeout, ct)
                : sessions is null
                    ? null
                    : await sessions.RunAsAsync(account, executable, manager.List, null, ListTimeout, ct);
            if (result is null)
            {
                return;
            }

            // A list that failed says nothing about what is installed. An empty one that succeeded
            // does, which is why the two are told apart here and not by counting rows.
            var software = result.ExitCode == 0 ? ManagerList.Parse(manager.Name, result.Output) : null;
            if (software is null)
            {
                logger.LogWarning("Could not read what {Manager} has installed (exit code {ExitCode})", manager.Name, result.ExitCode);
                return;
            }

            await PostAsync(settings, software, account, manager.Name, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not report what {Manager} has installed ({Reason})", manager.Name, ex.GetType().Name);
        }
    }

    private async Task PostAsync(PortalSettings settings, IReadOnlyList<InstalledSoftware> software, string? account,
        string? source, CancellationToken ct)
    {
        var query = new List<string>();
        if (account is not null)
        {
            query.Add("account=" + Uri.EscapeDataString(account));
        }

        if (source is not null)
        {
            query.Add("source=" + Uri.EscapeDataString(source));
        }

        var route = "api/v1/agent/software" + (query.Count == 0 ? "" : "?" + string.Join('&', query));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(settings.ServerUrl.TrimEnd('/') + "/"), route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.DeviceToken);
        request.Content = JsonContent.Create(software);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("The server refused the software report ({Status})", (int)response.StatusCode);
        }
    }
}
