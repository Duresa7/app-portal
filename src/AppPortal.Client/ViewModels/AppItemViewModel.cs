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

    public AppItemViewModel(CatalogApp app, Func<AppItemViewModel, Task> install)
    {
        _app = app;
        _install = install;
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
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(IsBusy), nameof(Percent), nameof(IsIndeterminate),
        nameof(NeedsRestart))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private InstallRequest? _activeInstall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(IsBusy), nameof(IsIndeterminate))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
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
                return active.State switch
                {
                    InstallState.Queued => "Queued, waiting for this PC",
                    InstallState.Running when active.RebootState == RebootState.Pending => RebootState.WaitingDetail,
                    InstallState.Running => $"Installing {step}{active.PercentComplete}%",
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
    [NotifyPropertyChangedFor(nameof(CanInstall))]
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

    /// <summary>
    /// Asks Windows to restart, with a minute's notice and a reason on screen. Nothing here forces it:
    /// the person pressed the button, and /t 60 leaves them time to save what they were doing.
    /// </summary>
    [RelayCommand]
    private void Restart()
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
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            LastError = "This PC could not be restarted from here. Restart it yourself to finish.";
        }
    }
}
