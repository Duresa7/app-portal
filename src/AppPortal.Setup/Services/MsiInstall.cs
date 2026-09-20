using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AppPortal.Setup.Services;

/// <summary>What the MSI needs to know, which is exactly what the wizard's second page asks for.</summary>
public sealed record MsiInstallRequest(string ServerUrl, string EnrollmentKey, string? Action1EndpointId);

/// <summary>
/// Running the package. The command line is built by a static function so a test can read it, because
/// a misplaced quote in an MSI property is the kind of mistake that only shows up on a real machine.
/// </summary>
public sealed class MsiInstall(IProcessRunner processes)
{
    /// <summary>
    /// Long enough for a slow disk and a service start on a tired PC, short enough that a hung install
    /// is reported to the tech rather than left on screen for the rest of the afternoon.
    /// </summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(15);

    public const string Program = "msiexec.exe";

    /// <summary>
    /// The msiexec command line. <c>/qn</c> because the wizard is the interface, <c>/norestart</c>
    /// because a restart is the deployment system's decision, and <c>/l*v</c> because the failure page
    /// is worth nothing without a log to point at.
    /// </summary>
    public static string Arguments(string package, string logPath, MsiInstallRequest request)
    {
        var line = new StringBuilder()
            .Append("/i \"").Append(package).Append('"')
            .Append(" /qn /norestart /l*v \"").Append(logPath).Append('"')
            .Append(" SERVERURL=\"").Append(request.ServerUrl).Append('"')
            .Append(" ENROLLMENTKEY=\"").Append(request.EnrollmentKey).Append('"');
        if (!string.IsNullOrWhiteSpace(request.Action1EndpointId))
        {
            line.Append(" ACTION1ENDPOINTID=\"").Append(request.Action1EndpointId).Append('"');
        }

        return line.ToString();
    }

    public Task<ProcessResult> RunAsync(string package, string logPath, MsiInstallRequest request, CancellationToken ct)
        => processes.RunAsync(Program, Arguments(package, logPath, request), Limit, ct);
}
