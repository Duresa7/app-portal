using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Setup.Services;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Setup.ViewModels;

public enum SetupPage
{
    Welcome,
    Server,
    Installing,
    Done,
    Failed,
}

/// <summary>
/// The wizard: Welcome, Server, Installing, and then Done or Failed. Everything it decides is decided
/// here rather than in the window, so the whole path a tech walks is under test on any operating system.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly SetupRun _run;
    private readonly IEnrollmentProbe _probe;
    private readonly CancellationTokenSource _cancellation = new();

    public SetupViewModel(SetupRun run, IEnrollmentProbe probe, SetupArguments? arguments = null)
    {
        _run = run;
        _probe = probe;
        ServerUrl = arguments?.ServerUrl ?? "";
        EnrollmentKey = arguments?.EnrollmentKey ?? "";
        Action1EndpointId = arguments?.Action1EndpointId ?? "";
    }

    /// <summary>Closes the window. Set by the view; a test leaves it null and reads the page instead.</summary>
    public Action? Close { get; set; }

    /// <summary>Puts the failure details on the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Starts the installed client. True when it started.</summary>
    public Func<string, bool>? Launch { get; set; }

    /// <summary>Where the MSI puts the client, and therefore what the Done page opens.</summary>
    public static string ClientPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "App Portal", "AppPortal.exe");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsServer), nameof(IsInstalling), nameof(IsDone), nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanCancel), nameof(NextText), nameof(CanContinue))]
    private SetupPage _page = SetupPage.Welcome;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private string _serverUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private string _enrollmentKey = "";

    [ObservableProperty]
    private string _action1EndpointId = "";

    /// <summary>
    /// True once the server has said the key enrolls for Action1. The field stays hidden until then:
    /// most keys do not need an endpoint id, and a box nobody can fill in is a box that stops people.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private bool _needsEndpointId;

    /// <summary>A check or an install is running; the buttons wait for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue), nameof(CanCancel), nameof(CanGoBack))]
    private bool _busy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private int _percent;

    /// <summary>What went wrong, on the Server page or on the Failed page. Empty when nothing has.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string _problem = "";

    [ObservableProperty]
    private string _logPath = "";

    [ObservableProperty]
    private string _deviceName = "";

    [ObservableProperty]
    private bool _restartRequired;

    /// <summary>What the process will return. The wizard is scriptable too, so it reports the same code.</summary>
    [ObservableProperty]
    private int _exitCode;

    public bool IsWelcome => Page == SetupPage.Welcome;

    public bool IsServer => Page == SetupPage.Server;

    public bool IsInstalling => Page == SetupPage.Installing;

    public bool IsDone => Page == SetupPage.Done;

    public bool IsFailed => Page == SetupPage.Failed;

    public bool HasProblem => Problem.Length > 0;

    public bool CanGoBack => !Busy && Page == SetupPage.Server;

    /// <summary>
    /// Whether the Cancel button belongs on this page. Not during the install, which must not be left
    /// half done, and not on the last page, which has a Close button of its own.
    /// </summary>
    public bool CanCancel => Page is not (SetupPage.Installing or SetupPage.Done);

    public string NextText => Page switch
    {
        SetupPage.Welcome => "Next",
        SetupPage.Server => "Install",
        _ => "Close",
    };

    public bool CanContinue => Page switch
    {
        SetupPage.Welcome => true,
        SetupPage.Server => !Busy
            && ServerUrl.Trim().Length > 0
            && EnrollmentKey.Trim().Length > 0
            && (!NeedsEndpointId || Action1EndpointId.Trim().Length > 0),
        SetupPage.Installing => false,
        _ => true,
    };

    /// <summary>The one thing Enter and the primary button do, whichever page is on screen.</summary>
    [RelayCommand]
    public async Task NextAsync()
    {
        if (!CanContinue)
        {
            return;
        }

        switch (Page)
        {
            case SetupPage.Welcome:
                Page = SetupPage.Server;
                break;
            case SetupPage.Server:
                await StartAsync();
                break;
            default:
                Close?.Invoke();
                break;
        }
    }

    [RelayCommand]
    public void Back()
    {
        if (CanGoBack)
        {
            Problem = "";
            Page = SetupPage.Welcome;
        }
    }

    /// <summary>
    /// Escape, the Cancel button, and Close on the last page. It refuses only while msiexec is running:
    /// a half-finished install is worse than any wait, and there is nothing useful to undo.
    /// </summary>
    [RelayCommand]
    public void Cancel()
    {
        if (Page == SetupPage.Installing)
        {
            return;
        }

        _cancellation.Cancel();
        Close?.Invoke();
    }

    /// <summary>Everything a support request needs, in one paste.</summary>
    [RelayCommand]
    public async Task CopyDetailsAsync()
    {
        if (CopyToClipboard is null)
        {
            return;
        }

        var details = string.Join(Environment.NewLine,
            $"App Portal setup on {Environment.MachineName}",
            $"Server: {ServerUrl.Trim()}",
            $"Exit code: {ExitCode}",
            $"Problem: {Problem}",
            $"Installer log: {(LogPath.Length > 0 ? LogPath : "none; nothing was installed")}");
        await CopyToClipboard(details);
    }

    [RelayCommand]
    public void OpenAppPortal()
    {
        if (Launch is not null && !Launch(ClientPath))
        {
            // The install worked, so this is a nuisance rather than a failure: say where the client is
            // and leave the page where it is, instead of closing on somebody who got nothing.
            Problem = $"App Portal is installed but did not start. Open it from the Start menu, or run {ClientPath}.";
            return;
        }

        Close?.Invoke();
    }

    /// <summary>
    /// Checks the server and the key, then installs. The check is first and separate so a wrong address
    /// or a spent key is a sentence on the Server page, not a failure after the MSI has run.
    /// </summary>
    private async Task StartAsync()
    {
        Busy = true;
        Problem = "";
        Status = "Checking the server";
        try
        {
            var reachable = await _probe.ReachAsync(ServerUrl, _cancellation.Token);
            if (!reachable.Ok)
            {
                Problem = reachable.Problem ?? "The server did not answer.";
                return;
            }

            var key = await _probe.CheckKeyAsync(ServerUrl, EnrollmentKey, _cancellation.Token);
            if (!key.Ok)
            {
                Problem = key.Problem ?? "That enrollment key is not usable.";
                return;
            }

            if (SetupEngine.NeedsEndpointId(key.Engine) && Action1EndpointId.Trim().Length == 0)
            {
                NeedsEndpointId = true;
                Problem = "This key enrolls devices through Action1, so it needs the Action1 endpoint id for this PC.";
                return;
            }

            // Only what the key really wants: an agent-only key has no endpoint to send.
            NeedsEndpointId = SetupEngine.NeedsEndpointId(key.Engine);
        }
        finally
        {
            Busy = false;
        }

        await InstallAsync();
    }

    private async Task InstallAsync()
    {
        Page = SetupPage.Installing;
        Busy = true;
        Percent = 0;
        Status = "Starting";
        var request = new MsiInstallRequest(
            ServerUrl.Trim(),
            EnrollmentKey.Trim(),
            NeedsEndpointId ? SetupArguments.Trimmed(Action1EndpointId) : null);
        SetupOutcome outcome;
        try
        {
            outcome = await _run.RunAsync(request, new Progress<SetupProgress>(Show), _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            outcome = new SetupOutcome(ExitCodes.EnrollmentFailed, ex.Message, LogPath, null);
        }
        finally
        {
            Busy = false;
        }

        ExitCode = outcome.ExitCode;
        LogPath = outcome.LogPath ?? "";
        RestartRequired = outcome.RestartRequired;
        if (outcome.Ok)
        {
            DeviceName = outcome.DeviceName ?? Environment.MachineName;
            Status = RestartRequired
                ? "Installed. Restart this PC to finish."
                : "Installed and enrolled.";
            Page = SetupPage.Done;
            return;
        }

        Problem = outcome.Problem ?? "The installation did not finish.";
        Page = SetupPage.Failed;
    }

    private void Show(SetupProgress progress)
    {
        Status = progress.Message;
        Percent = progress.Percent;
    }
}
