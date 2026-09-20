using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AppPortal.Setup.Services;

/// <summary>How far the two files have got. The wizard turns this into a line of text.</summary>
public sealed record EnrollmentProgress(bool Configured, bool HeartbeatSent);

/// <summary>Whether the PC became a device, and the token it now holds when it did.</summary>
public sealed record EnrollmentOutcome(bool Enrolled, string? DeviceToken, string? ServerUrl, string? Problem);

/// <summary>
/// Watching a folder until the agent has enrolled. The MSI only writes the key file; enrollment happens
/// afterwards, inside the service, so the only honest way to tell the tech it worked is to wait for the
/// two files the agent writes. The clock and the wait are arguments so a test drives sixty seconds of
/// polling against a temporary folder without spending any of them.
/// </summary>
public sealed class EnrollmentWatcher(
    string dataDirectory,
    Func<DateTimeOffset> now,
    Func<TimeSpan, CancellationToken, Task> wait)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The agent's first heartbeat comes within a few seconds; a minute covers a slow start.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>Where the agent keeps its state on Windows, which is where the MSI put the key file.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AppPortal");

    public EnrollmentWatcher(string dataDirectory)
        : this(dataDirectory, () => DateTimeOffset.UtcNow, Task.Delay)
    {
    }

    /// <summary>
    /// Waits for <c>client.json</c> to carry a device token and for <c>agent.json</c> to report a
    /// heartbeat that was sent after <paramref name="since"/>. The second condition is what tells this
    /// install apart from an older one whose files are still lying in the folder.
    /// </summary>
    public async Task<EnrollmentOutcome> WaitAsync(
        DateTimeOffset since,
        TimeSpan limit,
        IProgress<EnrollmentProgress>? progress,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var settings = Read<ClientConfiguration>("client.json");
            var status = Read<AgentStatusFile>("agent.json");
            var configured = !string.IsNullOrWhiteSpace(settings?.DeviceToken);
            var beat = status is not null
                && string.Equals(status.Outcome, "Succeeded", StringComparison.OrdinalIgnoreCase)
                && status.LastHeartbeatAt >= since;
            progress?.Report(new EnrollmentProgress(configured, beat));

            if (configured && beat)
            {
                return new EnrollmentOutcome(true, settings!.DeviceToken, settings.ServerUrl, null);
            }

            if (now() - since >= limit)
            {
                return new EnrollmentOutcome(false, null, null, Problem(configured, status, since));
            }

            await wait(Interval, ct);
        }
    }

    /// <summary>What to tell the tech, named after whichever half of the handshake did not happen.</summary>
    private static string Problem(bool configured, AgentStatusFile? status, DateTimeOffset since)
    {
        if (!configured)
        {
            return "The software installed, but this PC did not enroll. The agent could not reach the server "
                + "or the key was refused; the App Portal Agent service log has the reason.";
        }

        // A status file older than the install belongs to an earlier one, and says nothing about this.
        return status is null || status.LastHeartbeatAt < since
            ? "This PC enrolled, but the agent has not checked in since the install. It will keep trying on its own."
            : "This PC enrolled, but the agent's last check-in failed. It will keep trying on its own.";
    }

    private T? Read<T>(string name) where T : class
    {
        try
        {
            var path = Path.Combine(dataDirectory, name);
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Half-written, replaced under us, or not ours to read yet. The next pass gets it.
            return null;
        }
    }

    /// <summary>The part of the client's settings file this cares about.</summary>
    private sealed record ClientConfiguration(string? ServerUrl, string? DeviceToken);

    /// <summary>The agent's status file, as <c>HeartbeatWorker</c> writes it.</summary>
    private sealed record AgentStatusFile(DateTimeOffset LastHeartbeatAt, string Outcome);
}
