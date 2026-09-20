using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AppPortal.Setup.Services;

public enum SetupStage
{
    Checking,
    Installing,
    Enrolling,
    Finished,
}

/// <summary>One line of progress. <see cref="Percent"/> is what the bar shows, 0 to 100.</summary>
public sealed record SetupProgress(SetupStage Stage, string Message, int Percent);

/// <summary>
/// How the whole thing ended. <see cref="ExitCode"/> is what the process returns, so the wizard and the
/// silent run cannot drift apart on what counts as success.
/// </summary>
public sealed record SetupOutcome(int ExitCode, string? Problem, string? LogPath, string? DeviceName)
{
    public bool Ok => ExitCodes.Installed(ExitCode);

    public bool RestartRequired => ExitCode == ExitCodes.RestartRequired;
}

/// <summary>
/// Check the key, unpack the MSI, run it, wait for the PC to appear on the server, clean up. The wizard
/// and <c>/quiet</c> both go through this, which is the only way the two can be made to behave the same.
/// </summary>
public sealed class SetupRun(
    IMsiSource package,
    IEnrollmentProbe probe,
    MsiInstall installer,
    EnrollmentWatcher watcher,
    Func<DateTimeOffset> now,
    Func<string> scratchDirectory)
{
    /// <summary>
    /// A fresh folder under %TEMP% each run, so a second attempt never installs the package a first one
    /// half-wrote, and so the whole folder can be removed at the end without guessing what is in it.
    /// </summary>
    public static string DefaultScratchDirectory()
        => Path.Combine(Path.GetTempPath(), "AppPortalSetup-" + Guid.NewGuid().ToString("N")[..8]);

    public async Task<SetupOutcome> RunAsync(MsiInstallRequest request, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new SetupProgress(SetupStage.Checking, "Checking the enrollment key", 5));
        var key = await probe.CheckKeyAsync(request.ServerUrl, request.EnrollmentKey, ct);
        if (!key.Ok)
        {
            // Nothing has been written to this PC yet, and nothing will be.
            return new SetupOutcome(ExitCodes.EnrollmentFailed, key.Problem, null, null);
        }

        if (!package.Present)
        {
            return new SetupOutcome(ExitCodes.PackageUnavailable, EmbeddedMsiSource.MissingMessage, null, null);
        }

        var scratch = scratchDirectory();
        var logPath = Path.Combine(Path.GetTempPath(), "AppPortal-Setup.log");
        try
        {
            progress?.Report(new SetupProgress(SetupStage.Installing, "Installing App Portal", 20));
            var started = now();
            var packagePath = package.Extract(scratch);
            var result = await installer.RunAsync(packagePath, logPath, request, ct);
            if (!ExitCodes.Installed(result.ExitCode))
            {
                return new SetupOutcome(result.ExitCode, MsiProblem(result.ExitCode), logPath, null);
            }

            progress?.Report(new SetupProgress(SetupStage.Enrolling, "Enrolling this PC", 70));
            var enrollment = await watcher.WaitAsync(
                started,
                EnrollmentWatcher.Limit,
                new Relay<EnrollmentProgress>(step => progress?.Report(Step(step))),
                ct);
            if (!enrollment.Enrolled)
            {
                return new SetupOutcome(ExitCodes.EnrollmentFailed, enrollment.Problem, logPath, null);
            }

            var name = await probe.DeviceNameAsync(enrollment.ServerUrl ?? request.ServerUrl, enrollment.DeviceToken!, ct)
                ?? Environment.MachineName;
            progress?.Report(new SetupProgress(SetupStage.Finished, "Done", 100));
            return new SetupOutcome(ExitCodes.For(result.ExitCode, true), null, logPath, name);
        }
        finally
        {
            // The package is the one thing this leaves on disk that is worth removing. The log is not:
            // the failure page points at it, and a support request is worth more than a tidy %TEMP%.
            Discard(scratch);
        }
    }

    /// <summary>
    /// Passes a report straight on, on the thread that made it. <see cref="Progress{T}"/> would post it
    /// somewhere else, and a step about the install would then be able to arrive after the outcome.
    /// </summary>
    private sealed class Relay<T>(Action<T> forward) : IProgress<T>
    {
        public void Report(T value) => forward(value);
    }

    private static SetupProgress Step(EnrollmentProgress step)
        => step.Configured
            ? new SetupProgress(SetupStage.Enrolling, "Enrolled. Waiting for the agent to check in", 85)
            : new SetupProgress(SetupStage.Enrolling, "Enrolling this PC", 70);

    /// <summary>
    /// The msiexec codes a tech meets often enough to name. Anything else is passed through as a number,
    /// because a wrong guess at what a code means is worse than the code itself.
    /// </summary>
    private static string MsiProblem(int code) => code switch
    {
        1602 => "The installation was cancelled.",
        1603 => "Windows Installer could not complete the installation. The log has the step that failed.",
        1618 => "Another installation is already running. Let it finish and try again.",
        1619 => "Windows Installer could not open the package.",
        1625 => "A policy on this PC forbids installing this package.",
        _ => $"Windows Installer returned {code}.",
    };

    private static void Discard(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Windows still has the package open, or somebody else removed the folder first. %TEMP%
            // is cleaned by the system either way, and failing here would hide a successful install.
        }
    }
}
