using AppPortal.Setup.Services;
using AppPortal.Setup.ViewModels;

namespace AppPortal.Setup.Tests;

/// <summary>
/// The path a tech walks. Nothing here touches Avalonia, so the pages, the refusals and the keyboard
/// path are proved on whatever runs the tests rather than on a virtual machine somebody has to build.
/// </summary>
public sealed class SetupViewModelTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_wizard_opens_on_the_welcome_page_and_the_first_press_moves_on()
    {
        using var world = new World();
        var model = world.Model();

        Assert.True(model.IsWelcome);
        Assert.True(model.CanContinue);
        Assert.Equal("Next", model.NextText);

        model.NextCommand.Execute(null);

        Assert.True(model.IsServer);
        Assert.Equal("Install", model.NextText);
        Assert.True(model.CanGoBack);
    }

    [Fact]
    public void The_install_button_waits_for_both_fields()
    {
        using var world = new World();
        var model = world.Model();
        model.NextCommand.Execute(null);

        Assert.False(model.CanContinue);
        model.ServerUrl = "https://portal.example.internal";
        Assert.False(model.CanContinue);
        model.EnrollmentKey = "ape_ABC";
        Assert.True(model.CanContinue);
    }

    [Fact]
    public async Task A_key_the_server_refuses_is_a_message_on_the_page_and_nothing_installed()
    {
        using var world = new World();
        world.Probe.Key = ProbeResult.Bad("That enrollment key is not usable.");
        var model = world.Filled();

        await model.NextCommand.ExecuteAsync(null);

        Assert.True(model.IsServer);
        Assert.Equal("That enrollment key is not usable.", model.Problem);
        Assert.Empty(world.Processes.Commands);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_is_caught_before_the_key_is_sent_anywhere()
    {
        using var world = new World();
        world.Probe.Reach = ProbeResult.Bad("Could not reach https://portal.example.internal.");
        var model = world.Filled();

        await model.NextCommand.ExecuteAsync(null);

        Assert.True(model.IsServer);
        Assert.Contains("Could not reach", model.Problem);
        Assert.DoesNotContain(world.Probe.Asked, asked => asked.StartsWith("key "));
    }

    [Fact]
    public async Task An_Action1_key_asks_for_the_endpoint_id_before_it_installs()
    {
        using var world = new World();
        world.Probe.Key = ProbeResult.Good(SetupEngine.Both);
        var model = world.Filled();

        await model.NextCommand.ExecuteAsync(null);

        Assert.True(model.IsServer);
        Assert.True(model.NeedsEndpointId);
        Assert.Contains("Action1 endpoint id", model.Problem);
        Assert.Empty(world.Processes.Commands);
        // The button stays out until the new field has something in it.
        Assert.False(model.CanContinue);
        model.Action1EndpointId = "endpoint-1";
        Assert.True(model.CanContinue);
    }

    [Fact]
    public async Task An_agent_key_is_never_asked_for_an_endpoint_id()
    {
        using var world = new World();
        world.EnrollOnInstall();
        var model = world.Filled();

        await model.NextCommand.ExecuteAsync(null);

        Assert.False(model.NeedsEndpointId);
        Assert.True(world.Processes.Commands.TryDequeue(out var command));
        Assert.DoesNotContain("ACTION1ENDPOINTID", command);
    }

    [Fact]
    public async Task The_endpoint_id_reaches_msiexec_once_the_key_has_asked_for_one()
    {
        using var world = new World();
        world.Probe.Key = ProbeResult.Good(SetupEngine.Action1);
        world.EnrollOnInstall();
        var model = world.Filled();
        model.Action1EndpointId = "endpoint-1";

        await model.NextCommand.ExecuteAsync(null);

        Assert.True(model.IsDone);
        Assert.True(world.Processes.Commands.TryDequeue(out var command));
        Assert.Contains("ACTION1ENDPOINTID=\"endpoint-1\"", command);
    }

    [Fact]
    public async Task A_finished_install_lands_on_the_done_page_with_the_name_the_server_gave()
    {
        using var world = new World();
        world.Probe.DeviceName = "PC-RENAMED";
        world.EnrollOnInstall();
        var model = world.Filled();

        await model.NextCommand.ExecuteAsync(null);

        Assert.True(model.IsDone);
        Assert.Equal("PC-RENAMED", model.DeviceName);
        Assert.Equal(ExitCodes.Success, model.ExitCode);
        Assert.False(model.CanCancel);
    }

    [Fact]
    public async Task An_install_that_fails_lands_on_the_failed_page_with_the_log_path()
    {
        using var world = new World();
        world.Processes.ExitCode = 1603;
        var model = world.Filled();

        await model.NextCommand.ExecuteAsync(null);

        Assert.True(model.IsFailed);
        Assert.Equal(1603, model.ExitCode);
        Assert.Contains("could not complete", model.Problem);
        Assert.NotEqual("", model.LogPath);
    }

    [Fact]
    public async Task Copy_details_gathers_everything_a_support_request_needs()
    {
        using var world = new World();
        world.Processes.ExitCode = 1603;
        var model = world.Filled();
        var copied = "";
        model.CopyToClipboard = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };
        await model.NextCommand.ExecuteAsync(null);

        await model.CopyDetailsCommand.ExecuteAsync(null);

        Assert.Contains("https://portal.example.internal", copied);
        Assert.Contains("Exit code: 1603", copied);
        Assert.Contains(model.LogPath, copied);
        // Never the key: the details are meant to be pasted into a ticket.
        Assert.DoesNotContain("ape_ABC", copied);
    }

    [Fact]
    public async Task Nothing_can_be_pressed_while_the_install_runs()
    {
        using var world = new World();
        world.EnrollOnInstall();
        var model = world.Filled();
        var pages = new List<SetupPage>();
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SetupViewModel.Page))
            {
                pages.Add(model.Page);
            }
        };

        await model.NextCommand.ExecuteAsync(null);

        // Escape must not be able to abandon a half-finished msiexec, so Cancel is gone on that page.
        Assert.Contains(SetupPage.Installing, pages);
        Assert.Equal([SetupPage.Installing, SetupPage.Done], pages);
    }

    [Fact]
    public void Back_returns_to_the_welcome_page_and_clears_the_last_complaint()
    {
        using var world = new World();
        var model = world.Filled();
        model.Problem = "Something was wrong.";

        model.BackCommand.Execute(null);

        Assert.True(model.IsWelcome);
        Assert.Equal("", model.Problem);
    }

    [Fact]
    public void Escape_closes_the_wizard()
    {
        using var world = new World();
        var closed = false;
        var model = world.Model();
        model.Close = () => closed = true;

        model.CancelCommand.Execute(null);

        Assert.True(closed);
    }

    [Fact]
    public void A_command_line_fills_the_fields_in_for_the_tech()
    {
        using var world = new World();
        var arguments = SetupArguments.Parse(
            ["/server", "https://portal.example.internal", "/key", "ape_ABC", "/endpoint", "endpoint-1"]).Arguments;

        var model = world.Model(arguments);

        Assert.Equal("https://portal.example.internal", model.ServerUrl);
        Assert.Equal("ape_ABC", model.EnrollmentKey);
        Assert.Equal("endpoint-1", model.Action1EndpointId);
    }

    [Fact]
    public async Task Open_App_Portal_that_cannot_start_the_client_says_where_it_is()
    {
        using var world = new World();
        world.EnrollOnInstall();
        var model = world.Filled();
        model.Launch = _ => false;
        var closed = false;
        model.Close = () => closed = true;
        await model.NextCommand.ExecuteAsync(null);

        model.OpenAppPortalCommand.Execute(null);

        Assert.False(closed);
        Assert.Contains("Start menu", model.Problem);
    }

    /// <summary>Everything the wizard talks to, stood in for.</summary>
    private sealed class World : IDisposable
    {
        private readonly TemporaryFolder _data = new();
        private readonly string _scratch =
            Path.Combine(Path.GetTempPath(), "app-portal-setup-vm-" + Guid.NewGuid().ToString("N")[..8]);

        public FakeProbe Probe { get; } = new();

        public FakeProcessRunner Processes { get; } = new();

        public FakeClock Clock { get; } = new(Start);

        public void EnrollOnInstall()
            => Processes.WhileRunning = _ =>
            {
                _data.Write("client.json", """{"serverUrl":"https://portal.example.internal","deviceToken":"apd_token"}""");
                _data.Write("agent.json", $$"""{"lastHeartbeatAt":"{{Start:O}}","outcome":"Succeeded"}""");
            };

        public SetupViewModel Model(SetupArguments? arguments = null)
        {
            var run = new SetupRun(
                new FakeMsiSource(),
                Probe,
                new MsiInstall(Processes),
                new EnrollmentWatcher(_data.Path, Clock.Read, Clock.WaitAsync),
                Clock.Read,
                () => _scratch);
            return new SetupViewModel(run, Probe, arguments);
        }

        /// <summary>On the Server page with both fields filled, which is where most tests start.</summary>
        public SetupViewModel Filled()
        {
            var model = Model();
            model.NextCommand.Execute(null);
            model.ServerUrl = "https://portal.example.internal";
            model.EnrollmentKey = "ape_ABC";
            return model;
        }

        public void Dispose()
        {
            _data.Dispose();
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
    }
}
