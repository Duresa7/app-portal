using AppPortal.Server.Options;

using Microsoft.Extensions.Options;

namespace AppPortal.Server.Installs;

/// <summary>Refreshes every active install on a timer so history stays current even when no client is open.</summary>
public sealed class InstallStatusPoller(InstallStore store, InstallService installs, IOptions<PortalOptions> options, ILogger<InstallStatusPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.Value.StatusPollSeconds));
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var record in store.All().Where(r => r.IsActive))
            {
                try
                {
                    await installs.RefreshAsync(record, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Status refresh failed for install {Id}", record.Id);
                }
            }
        }
    }
}
