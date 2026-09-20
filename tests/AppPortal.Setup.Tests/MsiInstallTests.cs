using AppPortal.Setup.Services;

namespace AppPortal.Setup.Tests;

/// <summary>
/// The msiexec command line and the package that goes with it. A misplaced quote here is a deployment
/// that fails on every machine, and it is invisible in code review.
/// </summary>
public sealed class MsiInstallTests
{
    private static readonly MsiInstallRequest Request =
        new("https://portal.example.internal", "ape_ABC", null);

    [Fact]
    public void The_command_line_is_silent_leaves_the_restart_alone_and_keeps_a_verbose_log()
    {
        var arguments = MsiInstall.Arguments(@"C:\Temp\AppPortal.msi", @"C:\Temp\AppPortal-Setup.log", Request);

        Assert.Equal(
            """"
            /i "C:\Temp\AppPortal.msi" /qn /norestart /l*v "C:\Temp\AppPortal-Setup.log" SERVERURL="https://portal.example.internal" ENROLLMENTKEY="ape_ABC"
            """",
            arguments);
    }

    [Fact]
    public void Paths_with_spaces_stay_one_argument()
    {
        var arguments = MsiInstall.Arguments(
            @"C:\Users\Some One\AppData\Local\Temp\AppPortalSetup-ab12cd34\AppPortal.msi",
            @"C:\Users\Some One\AppData\Local\Temp\AppPortal-Setup.log",
            Request);

        Assert.Contains(@"/i ""C:\Users\Some One\AppData\Local\Temp\AppPortalSetup-ab12cd34\AppPortal.msi""", arguments);
        Assert.Contains(@"/l*v ""C:\Users\Some One\AppData\Local\Temp\AppPortal-Setup.log""", arguments);
    }

    [Fact]
    public void An_endpoint_id_is_passed_only_when_there_is_one()
    {
        Assert.DoesNotContain("ACTION1ENDPOINTID", MsiInstall.Arguments("p.msi", "l.log", Request));
        Assert.DoesNotContain("ACTION1ENDPOINTID", MsiInstall.Arguments("p.msi", "l.log", Request with { Action1EndpointId = "  " }));
        Assert.EndsWith(
            "ACTION1ENDPOINTID=\"endpoint-1\"",
            MsiInstall.Arguments("p.msi", "l.log", Request with { Action1EndpointId = "endpoint-1" }));
    }

    [Fact]
    public async Task The_runner_is_asked_for_msiexec_and_its_exit_code_comes_straight_back()
    {
        var processes = new FakeProcessRunner(1618);

        var result = await new MsiInstall(processes).RunAsync("p.msi", "l.log", Request, CancellationToken.None);

        Assert.Equal(1618, result.ExitCode);
        Assert.True(processes.Commands.TryDequeue(out var command));
        Assert.StartsWith("msiexec.exe /i", command);
    }

    [Fact]
    public void An_embedded_package_is_written_out_under_the_name_msiexec_will_log()
    {
        var directory = Path.Combine(Path.GetTempPath(), "app-portal-setup-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var source = new EmbeddedMsiSource(TestPackage.Assembly, TestPackage.ResourceName);

            Assert.True(source.Present);
            var path = source.Extract(directory);

            Assert.Equal(Path.Combine(directory, EmbeddedMsiSource.FileName), path);
            Assert.NotEmpty(File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_build_with_no_package_inside_it_says_so_instead_of_pretending()
    {
        // This is the Linux build of the solution, and any build made before the MSI exists. It has to
        // compile and run; what it must not do is look like a working installer.
        var source = new EmbeddedMsiSource(TestPackage.Assembly, "NoSuchPackage.msi");

        Assert.False(source.Present);
        var refused = Assert.Throws<InvalidOperationException>(() => source.Extract(Path.GetTempPath()));
        Assert.Contains("-p:AppPortalMsiPath=", refused.Message);
    }
}
