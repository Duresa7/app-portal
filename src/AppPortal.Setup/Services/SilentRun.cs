using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AppPortal.Setup.Services;

/// <summary>
/// <c>/quiet</c>: the same steps as the wizard with nobody watching. An RMM reads the exit code, so the
/// lines written here are for whoever reads the job's transcript afterwards.
/// </summary>
public static class SilentRun
{
    public static async Task<int> ExecuteAsync(SetupRun run, SetupArguments arguments, Action<string> write, CancellationToken ct)
    {
        var request = new MsiInstallRequest(
            arguments.ServerUrl!,
            arguments.EnrollmentKey!,
            arguments.Action1EndpointId);

        SetupOutcome outcome;
        try
        {
            outcome = await run.RunAsync(request, new Progress<SetupProgress>(step => write(step.Message)), ct);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            write(ex.Message);
            return ExitCodes.EnrollmentFailed;
        }

        if (outcome.Problem is { } problem)
        {
            write(problem);
        }

        if (outcome.LogPath is { } log)
        {
            write("Installer log: " + log);
        }

        if (outcome.Ok)
        {
            write($"Enrolled as {outcome.DeviceName}." + (outcome.RestartRequired ? " A restart is needed to finish." : ""));
        }

        return outcome.ExitCode;
    }
}
