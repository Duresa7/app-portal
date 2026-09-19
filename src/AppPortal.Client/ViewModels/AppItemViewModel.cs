using System;
using System.Threading.Tasks;
using AppPortal.Shared;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels;

/// <summary>One catalog card: the app, whether it is on this device, and any install in flight.</summary>
public sealed partial class AppItemViewModel : ViewModelBase
{
    private readonly Func<AppItemViewModel, Task> _install;

    public AppItemViewModel(CatalogApp app, Func<AppItemViewModel, Task> install)
    {
        App = app;
        _install = install;
    }

    public CatalogApp App { get; }

    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string Description => App.Description;
    public string Category => App.Category;
    public bool Featured => App.Featured;
    public string Initial => string.IsNullOrEmpty(App.Name) ? "?" : App.Name[..1].ToUpperInvariant();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(IsBusy), nameof(Percent))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private InstallRequest? _activeInstall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanInstall), nameof(IsBusy))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isRequesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _lastError;

    public bool IsBusy => IsRequesting || ActiveInstall is not null;

    public bool CanInstall => !IsBusy && !IsInstalled;

    public int Percent => ActiveInstall?.PercentComplete ?? 0;

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
                return "Sending request...";
            }

            if (ActiveInstall is { } active)
            {
                return active.State switch
                {
                    InstallState.Queued => "Queued. Waiting for the device to pick it up.",
                    InstallState.Running => $"Installing... {active.PercentComplete}%",
                    _ => active.State.ToString(),
                };
            }

            return IsInstalled ? "Installed" : "Not installed";
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync() => _install(this);
}
