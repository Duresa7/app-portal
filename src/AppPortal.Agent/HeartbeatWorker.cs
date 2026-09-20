using System.Diagnostics;
using System.Text.Json;

using AppPortal.Agent.Enrollment;
using AppPortal.Shared;

namespace AppPortal.Agent;

/// <summary>
/// What the agent last managed, written to agent.json where any user can read it. The client shows it
/// in diagnostics later, which is why it is readable rather than kept to the service account.
/// </summary>
public sealed record AgentStatus(DateTimeOffset LastHeartbeatAt, string Outcome);

public sealed class HeartbeatWorker(
    HeartbeatClient client,
    ILogger<HeartbeatWorker> logger,
    IHostApplicationLifetime lifetime,
    string statusPath,
    bool once,
    EnrollmentService? enrollment = null) : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public int ExitCode { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = 900;
        while (!stoppingToken.IsCancellationRequested)
        {
            var outcome = "Succeeded";
            var enrollmentComplete = false;
            try
            {
                if (enrollment is not null)
                {
                    await enrollment.EnsureEnrolledAsync(stoppingToken);
                }

                enrollmentComplete = true;
                // Reload on each attempt so enrollment and token rotation do not need a service restart.
                var settings = PortalSettings.Load();
                var version = typeof(HeartbeatWorker).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
                var heartbeat = new AgentHeartbeatRequest(version, ClientVersion(), Environment.OSVersion.VersionString);
                var answer = await client.SendAsync(settings, heartbeat, stoppingToken);
                seconds = answer.HeartbeatSeconds;
                ExitCode = 0;
                logger.LogInformation("Heartbeat accepted; next interval {Seconds} seconds", seconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
                                       or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or NotSupportedException or ArgumentException)
            {
                outcome = "Failed";
                ExitCode = 1;
                if (!enrollmentComplete)
                {
                    // A PC installed before the network is ready should not wait a whole heartbeat interval.
                    seconds = 30;
                }
                logger.LogWarning("Heartbeat failed ({Reason}); will retry", ex.GetType().Name);
            }

            WriteStatus(new AgentStatus(DateTimeOffset.UtcNow, outcome));
            if (once)
            {
                lifetime.StopApplication();
                return;
            }

            try
            {
                await Task.Delay(NextDelay(seconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public static TimeSpan NextDelay(int seconds)
        => TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 86400) * (0.9 + Random.Shared.NextDouble() * 0.2));

    private void WriteStatus(AgentStatus status)
    {
        try
        {
            // Readers should see one whole result even if they open the file while it is replaced.
            File.WriteAllText(statusPath + ".tmp", JsonSerializer.Serialize(status, Json));
            File.Move(statusPath + ".tmp", statusPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not write agent status ({Reason})", ex.GetType().Name);
        }
    }

    private static string? ClientVersion()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "AppPortal.exe");
        return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).ProductVersion : null;
    }
}
