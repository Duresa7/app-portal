using AppPortal.Setup.Services;

namespace AppPortal.Setup.Tests;

/// <summary>
/// The number the deployment system reads. Anything wrong here is wrong on every machine at once, and
/// it is invisible until somebody wonders why a fleet reports success it never had.
/// </summary>
public sealed class ExitCodeTests
{
    [Fact]
    public void An_install_that_enrolled_is_a_success()
    {
        Assert.Equal(ExitCodes.Success, ExitCodes.For(0, enrolled: true));
    }

    [Fact]
    public void An_install_that_did_not_enroll_says_so_rather_than_reporting_success()
    {
        Assert.Equal(ExitCodes.EnrollmentFailed, ExitCodes.For(0, enrolled: false));
    }

    [Fact]
    public void A_restart_pending_install_keeps_its_own_code()
    {
        // 3010 is the only way the deployment system learns it has a restart to schedule.
        Assert.Equal(ExitCodes.RestartRequired, ExitCodes.For(ExitCodes.RestartRequired, enrolled: true));
        Assert.True(ExitCodes.Installed(ExitCodes.RestartRequired));
    }

    [Theory]
    [InlineData(1603)]
    [InlineData(1618)]
    [InlineData(1625)]
    public void An_install_that_failed_keeps_the_code_msiexec_gave(int code)
    {
        // Never turned into 1: the software is not on the machine, so enrollment was never the problem.
        Assert.Equal(code, ExitCodes.For(code, enrolled: false));
        Assert.Equal(code, ExitCodes.For(code, enrolled: true));
        Assert.False(ExitCodes.Installed(code));
    }
}
