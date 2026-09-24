using System;
using System.Threading.Tasks;

using AppPortal.Shared;

using Avalonia.Media;
using Avalonia.Media.Imaging;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels;

/// <summary>One catalog card: the app, whether it is on this device, and any install in flight.</summary>
public sealed partial class AppItemViewModel : ViewModelBase
{
    // Fluent 2 accent-adjacent hues, picked so neighbouring cards in the grid do not repeat a color.
    private static readonly string[] TilePalette =
    [
        "#0F6CBD", "#0E7C42", "#8764B8", "#C239B3", "#B146C2",
        "#986F0B", "#C4314B", "#038387", "#4F6BED", "#CA5010",
    ];

    private readonly Func<AppItemViewModel, Task> _install;

    private readonly Func<AppItemViewModel, Task> _remove;

    public AppItemViewModel(CatalogApp app, Func<AppItemViewModel, Task> install, Func<AppItemViewModel, Task>? remove = null)
    {
        _app = app;
        _install = install;
        _remove = remove ?? (_ => Task.CompletedTask);
        TileBrush = new SolidColorBrush(Color.Parse(TilePalette[StableIndex(app.Id, TilePalette.Length)]));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Publisher), nameof(Description), nameof(Category), nameof(Featured), nameof(Initial), nameof(DownloadSizeText), nameof(HasDownloadSize))]
    private CatalogApp _app;

    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string Description => App.Description;
    public string Category => App.Category;
    public bool Featured => App.Featured;
    /// <summary>
    /// Said before the install rather than discovered after it. A per-user install lands in the
    /// profile of whoever asked, so it is not on the PC for anyone else, and somebody who expects
    /// otherwise should find that out here.
    /// </summary>
    public bool InstallsForYouOnly => App.InstallScope == "user";

    /// <summary>
    /// Which engine would carry this out, so that a person reporting a problem and the administrator
    /// reading the history are looking at the same word.
    /// </summary>
    public string EngineText => EngineLabel.For(App.Engine);

    public bool HasEngine => !string.IsNullOrEmpty(App.Engine);

    /// <summary>
    /// The software is on this PC and will not work until it restarts. Asked for rather than done: a
    /// portal that restarts somebody's machine on its own is a portal people turn off.
    /// </summary>
    public bool NeedsRestart => ActiveInstall?.RebootState == RebootState.Pending;

    public string RestartText => $"Restart this PC to finish installing {App.Name}.";

    /// <summary>
    /// Offered only on what is actually on the PC, and only when the administrator has said this app
    /// may be taken off by the person who put it there.
    /// </summary>
    public bool CanRemove => App.UserRemovable && IsInstalled && !IsBusy && !IsConfirming;

    public bool HasRequirements => !string.IsNullOrWhiteSpace(App.Requirements);

    public string RequirementsText => App.Requirements ?? "";

    public bool HasDownloadSize => App.DownloadSizeBytes is not null;
    public string DownloadSizeText => App.DownloadSizeBytes switch
    {
        null => "",
        >= 1_000_000_000 => $"{App.DownloadSizeBytes / 1_000_000_000d:0.#} GB download",
        >= 1_000_000 => $"{App.DownloadSizeBytes / 1_000_000d:0.#} MB download",
        >= 1_000 => $"{App.DownloadSizeBytes / 1_000d:0.#} KB download",
        _ => $"{App.DownloadSizeBytes} bytes download",
    };
    public string Initial => string.IsNullOrEmpty(App.Name) ? "?" : App.Name[..1].ToUpperInvariant();

    /// <summary>Backplate for the lettered tile, stable per app so the grid looks deliberate rather than random.</summary>
    public IBrush TileBrush { get; }

    [ObservableProperty]
    private Bitmap? _icon;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(IsBusy), nameof(CanRemove))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(RemoveCommand))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(CanRemove), nameof(IsBusy), nameof(Percent),
        nameof(IsIndeterminate), nameof(NeedsRestart))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(RemoveCommand))]
    private InstallRequest? _activeInstall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(CanRemove), nameof(IsBusy), nameof(IsIndeterminate))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(RemoveCommand))]
    private bool _isRequesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasError))]
    private string? _lastError;

    public bool IsBusy => IsRequesting || ActiveInstall is not null;

    public bool CanInstall => !IsBusy && !IsInstalled && !IsConfirming;

    public bool HasError => LastError is not null;

    public int Percent => ActiveInstall?.PercentComplete ?? 0;

    /// <summary>Queued work has no meaningful percentage, so the bar animates instead of sitting at zero.</summary>
    public bool IsIndeterminate => IsRequesting || ActiveInstall?.State == InstallState.Queued;

    public string StatusText
    {
        get
        {
            if (LastError is not null)
            {
                return LastError;
            }

            if (IsRequesting)
            {
                return "Sending request";
            }

            if (ActiveInstall is { } active)
            {
                // An app that needed others first installs as one thing with several steps, and
                // "Installing, 40%" over and over tells the person nothing about which of them.
                var step = active.StepCount > 1 ? $"{active.StepName} ({active.StepNumber} of {active.StepCount}), " : "";
                // A removal runs through the same row and the same states, so the card has to say which
                // of the two the person is watching.
                var verb = active.Kind == InstallKind.Uninstall ? "Removing" : "Installing";
                return active.State switch
                {
                    InstallState.Queued => "Queued, waiting for this PC",
                    InstallState.Running when active.RebootState == RebootState.Pending => RebootState.WaitingDetail,
                    InstallState.Running => $"{verb} {step}{active.PercentComplete}%",
                    _ => active.State.ToString(),
                };
            }

            return IsInstalled ? "Installed" : "Not installed";
        }
    }

    /// <summary>
    /// FNV-1a over the app id. String.GetHashCode is randomized per process in .NET, so using it here
    /// would repaint every tile a different color on each launch.
    /// </summary>
    private static int StableIndex(string value, int buckets)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in value)
            {
                hash = (hash ^ c) * 16777619u;
            }

            return (int)(hash % (uint)buckets);
        }
    }

    /// <summary>
    /// True once Install has been pressed on an app that states requirements, and until the person has
    /// either agreed to them or backed out. The text is on the card either way; this is what stops it
    /// being scrolled past, on the apps that have something to say and on no others.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(CanRemove))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private bool _isConfirming;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync()
    {
        if (HasRequirements && !IsConfirming)
        {
            IsConfirming = true;
            return Task.CompletedTask;
        }

        IsConfirming = false;
        return _install(this);
    }

    [RelayCommand]
    private Task ConfirmInstallAsync()
    {
        IsConfirming = false;
        return _install(this);
    }

    [RelayCommand]
    private void CancelInstall() => IsConfirming = false;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private Task RemoveAsync() => _remove(this);

    /// <summary>
    /// Asks Windows to restart, with a minute's notice and a reason on screen. Nothing here forces it:
    /// the person pressed the button, and /t 60 leaves them time to save what they were doing. For the
    /// same reason Windows refuses it while somebody else is signed in, and shutdown.exe says so only in
    /// its exit code, so the exit code is read rather than the restart assumed.
    /// </summary>
    [RelayCommand]
    private async Task RestartAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            using var shutdown = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe",
                $"/g /t 60 /c \"App Portal is finishing the installation of {App.Name}.\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (shutdown is null)
            {
                LastError = RestartError(-1);
                return;
            }

            // It only schedules the restart, so it answers at once; the minute's notice runs in Windows.
            using var wait = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            await shutdown.WaitForExitAsync(wait.Token);
            if (RestartError(shutdown.ExitCode) is { } error)
            {
                LastError = error;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException
                                       or OperationCanceledException)
        {
            LastError = RestartError(-1);
        }
    }

    /// <summary>ERROR_SHUTDOWN_USERS_LOGGED_ON: another account is signed in and the restart was not forced.</summary>
    public const int OtherPeopleSignedIn = 1191;

    /// <summary>What the card says when shutdown.exe did not schedule the restart, or null when it did.</summary>
    public static string? RestartError(int exitCode) => exitCode switch
    {
        0 => null,
        OtherPeopleSignedIn => "Someone else is signed in to this PC, and Windows will not restart it while they are. Ask them to sign out, then restart.",
        _ => "This PC could not be restarted from here. Restart it yourself to finish.",
    };
}
