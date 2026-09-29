using AppPortal.Agent.Sessions;
using AppPortal.Shared;

namespace AppPortal.Agent.Jobs;

/// <summary>
/// What each person's own profile carries, reported once every time they sign in.
/// </summary>
/// <remarks>
/// Software in a profile can only be listed from inside that person's session, so the sweep at service
/// start covers the machine-wide list and nothing else. Before this, a person's list was swept only
/// straight after the portal installed something for them, and anything they added or removed on their
/// own stayed wrong until the next install. A sign-in is the moment their session exists, and after a
/// restart it is the first moment their list can be read at all.
///
/// The sweep waits a while after the account appears. At the first sign-in to a profile Windows
/// registers the App Installer package, and with it the winget alias, some time after the desktop is up,
/// and a list read before that fails. A failed list is harmless, because the reporter keeps the last
/// report, but it is also useless, so the sweep gives the session time to settle first.
/// </remarks>
public sealed class SignInSoftwareSweep(
    SoftwareReporter software,
    IUserSessionLauncher sessions,
    ILogger<SignInSoftwareSweep> logger,
    Func<PortalSettings>? loadSettings = null,
    TimeSpan? pollInterval = null,
    TimeSpan? settleTime = null) : BackgroundService
{
    private readonly Func<PortalSettings> _loadSettings = loadSettings ?? (() => PortalSettings.Load());
    private readonly TimeSpan _poll = pollInterval ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan _settle = settleTime ?? TimeSpan.FromSeconds(60);

    // Everyone who was signed in at the last poll, in the order they arrived. Nobody at service start,
    // so whoever is signed in then is swept once: an upgrade restarts the service under their feet, and
    // the list from before it may be the list of an older agent.
    private readonly List<Arrival> _present = [];

    /// <summary>
    /// One look at who is signed in. Sweeps every account that has been signed in for the settle time
    /// and has not been swept in this sign-in, and returns them in the order it swept them.
    /// </summary>
    internal async Task<IReadOnlyList<string>> PollAsync(DateTimeOffset now, CancellationToken ct)
    {
        var signedIn = sessions.SignedInAccounts();

        // Gone at this poll is signed out, and the next sign-in is a new one that is swept again.
        _present.RemoveAll(arrival => !signedIn.Contains(arrival.Account, StringComparer.OrdinalIgnoreCase));
        foreach (var account in signedIn)
        {
            if (!_present.Any(arrival => string.Equals(arrival.Account, account, StringComparison.OrdinalIgnoreCase)))
            {
                _present.Add(new Arrival(account, now));
            }
        }

        var due = _present.Where(arrival => !arrival.Swept && now - arrival.Since >= _settle).ToList();
        if (due.Count == 0)
        {
            return [];
        }

        var settings = _loadSettings();
        if (!settings.IsConfigured)
        {
            // Not enrolled yet. They stay due, so the first poll after enrollment sweeps them.
            return [];
        }

        var swept = new List<string>();
        foreach (var arrival in due)
        {
            logger.LogInformation("Reporting what {Account} has installed, because they signed in", arrival.Account);
            await software.ReportAsync(settings, ct, arrival.Account);
            await software.ReportManagersAsync(settings, ct, arrival.Account);

            // Swept even when the reporter could not read the list: it has said why in the log, and
            // asking again every thirty seconds would only say it again.
            arrival.Swept = true;
            swept.Add(arrival.Account);
        }

        return swept;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(DateTimeOffset.UtcNow, stoppingToken);
                await Task.Delay(_poll, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A service that stops because one poll failed stops installing too.
                logger.LogWarning("Could not check who has signed in ({Reason})", ex.GetType().Name);
                try
                {
                    await Task.Delay(_poll, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private sealed class Arrival(string account, DateTimeOffset since)
    {
        public string Account { get; } = account;

        public DateTimeOffset Since { get; } = since;

        public bool Swept { get; set; }
    }
}
