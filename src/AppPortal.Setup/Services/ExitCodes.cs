namespace AppPortal.Setup.Services;

/// <summary>
/// What <c>AppPortalSetup.exe</c> returns. An RMM reads this and nothing else, so the whole mapping is
/// one pure function here rather than a return statement beside each thing that can go wrong.
/// </summary>
public static class ExitCodes
{
    public const int Success = 0;

    /// <summary>The software is on the device but it did not become a device on the server.</summary>
    public const int EnrollmentFailed = 1;

    public const int InvalidArguments = 2;

    /// <summary>
    /// Windows Installer's "installed, and this PC has to restart before it is finished". Passed back
    /// unchanged, because a deployment system that is told 0 will never schedule that restart.
    /// </summary>
    public const int RestartRequired = 3010;

    /// <summary>
    /// Windows Installer's "this package could not be opened", which is what a build with no MSI inside
    /// it amounts to. Reported as an installer error rather than an argument error: the command was
    /// right and the executable is wrong.
    /// </summary>
    public const int PackageUnavailable = 1620;

    /// <summary>Whether msiexec left the software on the machine, restart pending or not.</summary>
    public static bool Installed(int msiExitCode) => msiExitCode is Success or RestartRequired;

    /// <summary>
    /// The exit code for a run that got as far as msiexec. Enrollment can only fail after a successful
    /// install, so a failed install keeps its own code and never turns into <see cref="EnrollmentFailed"/>.
    /// </summary>
    public static int For(int msiExitCode, bool enrolled)
    {
        if (!Installed(msiExitCode))
        {
            return msiExitCode;
        }

        return enrolled ? msiExitCode : EnrollmentFailed;
    }
}
