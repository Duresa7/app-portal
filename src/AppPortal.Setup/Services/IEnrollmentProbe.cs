using System.Threading;
using System.Threading.Tasks;

namespace AppPortal.Setup.Services;

/// <summary>
/// The engine names <c>GET /api/v1/enroll/check</c> answers with. A key that enrolls for Action1 needs
/// an endpoint id, and this is how the wizard knows to ask for one before it installs anything.
/// </summary>
public static class SetupEngine
{
    public const string Action1 = "action1";
    public const string Agent = "agent";
    public const string Both = "both";

    public static bool NeedsEndpointId(string? engine) => engine is Action1 or Both;
}

/// <summary>
/// The answer to one question asked of the server. <see cref="Problem"/> is what to put on screen when
/// <see cref="Ok"/> is false, worded for the person at the keyboard rather than for a log.
/// </summary>
public sealed record ProbeResult(bool Ok, string? Problem, string? Engine = null)
{
    public static ProbeResult Good(string? engine = null) => new(true, null, engine);

    public static ProbeResult Bad(string problem) => new(false, problem);
}

/// <summary>
/// Everything the wizard asks the server before and after the install, behind one interface so the
/// whole flow can be driven from a test with no server and no network.
/// </summary>
public interface IEnrollmentProbe
{
    /// <summary>Whether the server answers at all, asked of <c>/healthz</c>.</summary>
    Task<ProbeResult> ReachAsync(string serverUrl, CancellationToken ct);

    /// <summary>Whether the key is still usable, and which engine it enrolls for. Spends no use of it.</summary>
    Task<ProbeResult> CheckKeyAsync(string serverUrl, string enrollmentKey, CancellationToken ct);

    /// <summary>
    /// The device's name as the server recorded it, for the last page. Null when the call did not
    /// answer: the install has already succeeded by then, so this is never worth failing over.
    /// </summary>
    Task<string?> DeviceNameAsync(string serverUrl, string deviceToken, CancellationToken ct);
}
