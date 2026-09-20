using AppPortal.Setup.Services;

namespace AppPortal.Setup.Tests;

/// <summary>
/// The command line an RMM types once and then copies onto a thousand machines. Every refusal here is
/// one that would otherwise be found by a failed deployment, so they are all worth a test.
/// </summary>
public sealed class SetupArgumentsTests
{
    [Fact]
    public void No_arguments_means_the_wizard()
    {
        var parsed = SetupArguments.Parse([]);

        Assert.Null(parsed.Error);
        Assert.False(parsed.Arguments!.Quiet);
        Assert.Null(parsed.Arguments.ServerUrl);
    }

    [Fact]
    public void A_full_silent_command_line_is_read_whole()
    {
        var parsed = SetupArguments.Parse(
            ["/quiet", "/server", "https://portal.example.internal", "/key", "ape_ABC", "/endpoint", "endpoint-1"]);

        var arguments = parsed.Arguments!;
        Assert.Null(parsed.Error);
        Assert.True(arguments.Quiet);
        Assert.Equal("https://portal.example.internal", arguments.ServerUrl);
        Assert.Equal("ape_ABC", arguments.EnrollmentKey);
        Assert.Equal("endpoint-1", arguments.Action1EndpointId);
    }

    [Theory]
    [InlineData("/quiet")]
    [InlineData("-quiet")]
    [InlineData("--quiet")]
    [InlineData("/QUIET")]
    public void The_switch_may_be_written_the_way_the_deployment_system_writes_it(string quiet)
    {
        var parsed = SetupArguments.Parse([quiet, "/server", "https://portal.example.internal", "/key", "ape_ABC"]);

        Assert.Null(parsed.Error);
        Assert.True(parsed.Arguments!.Quiet);
    }

    [Fact]
    public void Silent_mode_without_a_server_or_a_key_is_refused()
    {
        Assert.Equal("/quiet needs both /server and /key.", SetupArguments.Parse(["/quiet"]).Error);
        Assert.Equal(
            "/quiet needs both /server and /key.",
            SetupArguments.Parse(["/quiet", "/server", "https://portal.example.internal"]).Error);
    }

    [Fact]
    public void A_switch_with_nothing_after_it_is_refused()
    {
        Assert.Equal("/server needs a value after it.", SetupArguments.Parse(["/server"]).Error);
        // The next word is a switch, so the value was forgotten rather than given.
        Assert.Equal("/key needs a value after it.", SetupArguments.Parse(["/key", "/quiet"]).Error);
    }

    [Fact]
    public void A_switch_nobody_recognises_is_refused_rather_than_ignored()
    {
        Assert.Equal("/norestart is not one of this installer's switches.", SetupArguments.Parse(["/norestart"]).Error);
        Assert.Equal("'oops' is not one of this installer's switches.", SetupArguments.Parse(["oops"]).Error);
    }

    [Theory]
    [InlineData("portal.example.internal")]
    [InlineData("ftp://portal.example.internal")]
    public void A_server_address_that_is_not_an_http_url_is_refused(string url)
    {
        Assert.Equal(
            "The server address has to start with https:// or http://.",
            SetupArguments.Parse(["/server", url]).Error);
    }

    [Theory]
    [InlineData("https://user:pass@portal.example.internal")]
    [InlineData("https://portal.example.internal/?a=b")]
    [InlineData("https://portal.example.internal/#frag")]
    public void A_server_address_carrying_more_than_an_address_is_refused(string url)
    {
        Assert.Equal(
            "Give the server's address on its own, without a sign-in, a query or a fragment.",
            SetupArguments.Parse(["/server", url]).Error);
    }

    [Fact]
    public void Values_the_MSI_would_reject_are_refused_before_anything_is_installed()
    {
        // Package.wxs refuses these because they would change the JSON it writes. Catching them here
        // means the tech is told, rather than the MSI failing halfway through a deployment.
        Assert.Equal(
            "The enrollment key must not contain quotes, backslashes, tabs or line breaks.",
            SetupArguments.Parse(["/key", "ape_\"OR1=1"]).Error);
        Assert.Equal(
            "The Action1 endpoint id must not contain quotes, backslashes, tabs or line breaks.",
            SetupArguments.Parse(["/endpoint", @"a\b"]).Error);
    }

    [Fact]
    public void Surrounding_space_from_a_paste_is_dropped()
    {
        var parsed = SetupArguments.Parse(["/server", " https://portal.example.internal ", "/key", " ape_ABC "]);

        Assert.Equal("https://portal.example.internal", parsed.Arguments!.ServerUrl);
        Assert.Equal("ape_ABC", parsed.Arguments.EnrollmentKey);
    }

    [Fact]
    public void Help_is_a_request_rather_than_a_mistake()
    {
        var parsed = SetupArguments.Parse(["/?"]);

        Assert.Null(parsed.Error);
        Assert.True(parsed.Arguments!.Help);
    }
}
