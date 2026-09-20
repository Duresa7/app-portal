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
    WingetLocator? locator = null)
{
    private const string ListArguments = "list --accept-source-agreements --disable-interactivity";

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

            ProcessResult? result;
            if (account is null)
            {
                result = await processes.RunAsync(executable, ListArguments, null, TimeSpan.FromMinutes(5), ct);
            }
            else if (sessions is null)
            {
                return;
            }
            else
            {
                result = await sessions.RunAsAsync(account, executable, ListArguments, null, TimeSpan.FromMinutes(5), ct);
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
                logger.LogWarning("Could not read the installed software list; leaving the last report alone");
                return;
            }

            var route = account is null
                ? "api/v1/agent/software"
                : "api/v1/agent/software?account=" + Uri.EscapeDataString(account);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The install worked. Failing to describe it afterwards must not turn that into a failure.
            logger.LogWarning("Could not report installed software ({Reason})", ex.GetType().Name);
        }
    }
}
