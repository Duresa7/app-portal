using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Client.Services;
using AppPortal.Shared;

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    /// <summary>Shown when the request file cannot be left. Nothing a person at this PC can fix alone.</summary>
    private const string AgentUnreachable =
        "The update could not be requested. An administrator can check that the App Portal Agent service is running on this PC.";

    private readonly IPortalApiClient? _api;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly IconCache _icons = new();
    private IReadOnlyList<InstalledApp> _installedRaw = [];

    public MainViewModel() : this(null, new ClientSettings(), false)
    {
    }

    public MainViewModel(IPortalApiClient? api, ClientSettings settings, bool isDemo = false)
    {
        _api = api;
        IsDemo = isDemo;
        IsConfigured = api is not null && (settings.IsConfigured || isDemo);
        ConfigPath = ClientSettings.DefaultPath;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(Math.Max(3, settings.RefreshSeconds)), DispatcherPriority.Background, async (_, _) => await TickAsync());
        if (IsConfigured)
        {
            _timer.Start();
        }

        ReadUpdateStatus();
    }

    public bool IsConfigured { get; }

    /// <summary>True when the app was started with --demo: sample data, no server, nothing leaves the machine.</summary>
    public bool IsDemo { get; }

    public string ConfigPath { get; }

    public string AppVersion { get; } = UpdateStatusReader.RunningVersion;

    public string AppVersionText => $"App Portal {AppVersion}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateReady), nameof(UpdateAvailable), nameof(UpdateText))]
    private UpdateStatus? _updateStatus;

    /// <summary>Set after the user asks for an update, so the banner can show progress until the agent reports back.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateAvailable), nameof(UpdateText))]
    private bool _updateRequested;

    /// <summary>A newer build is downloaded and verified; one restart finishes it.</summary>
    public bool UpdateReady => !IsDemo && VersionText.IsNewer(UpdateStatus?.StagedVersion, AppVersion);

    /// <summary>A newer build is published but not yet on this machine.</summary>
    public bool UpdateAvailable => !IsDemo && !UpdateReady && VersionText.IsNewer(UpdateStatus?.LatestVersion, AppVersion);

    public string UpdateText
    {
        get
        {
            if (UpdateReady)
            {
                return $"App Portal {UpdateStatus!.StagedVersion} is ready. Restart the app to finish updating.";
            }

            if (UpdateRequested && UpdateStatus?.Result == UpdateResult.Failed)
            {
                return $"The update did not complete. {UpdateStatus.Message}";
            }

            if (UpdateRequested)
            {
                return $"Downloading App Portal {UpdateStatus?.LatestVersion}. This takes a moment.";
            }

            return $"App Portal {UpdateStatus?.LatestVersion} is available. It installs on its own within a day, or now if you like.";
        }
    }

    public ObservableCollection<AppItemViewModel> Apps { get; } = [];
    public ObservableCollection<AppItemViewModel> FilteredApps { get; } = [];
    public ObservableCollection<InstalledApp> Installed { get; } = [];
    public ObservableCollection<InstallRequest> Installs { get; } = [];
    public ObservableCollection<ActivityItemViewModel> Activity { get; } = [];
    public ObservableCollection<RequestItemViewModel> Requests { get; } = [];
    public ObservableCollection<string> Categories { get; } = ["All"];

    [ObservableProperty] private DeviceInfo? _device;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private DateTimeOffset? _lastRefreshed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApps), nameof(ShowInstalled), nameof(ShowActivity), nameof(ShowRequests))]
    private int _selectedSection;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _requestText = "";
    [ObservableProperty] private string? _requestNotice;
    [ObservableProperty] private string _selectedCategory = "All";

    /// <summary>The server refuses anything longer, so the box stops the user before the round trip does.</summary>
    public int RequestMaxLength => AppRequestLimits.MaxTextLength;

    public bool ShowApps => SelectedSection == 0;
    public bool ShowInstalled => SelectedSection == 1;
    public bool ShowActivity => SelectedSection == 2;
    public bool ShowRequests => SelectedSection == 3;

    public string DeviceTitle => Device?.DeviceName ?? Environment.MachineName;

    public string DeviceSubtitle
    {
        get
        {
            if (Device is null)
            {
                return IsConfigured ? "Connecting..." : "Not configured";
            }

            var seen = Device.LastSeen is { } t ? $"last seen {t.ToLocalTime():g}" : "last seen unknown";
            return $"{Device.EndpointStatus}, {seen}";
        }
    }

    public int ActiveInstallCount => Installs.Count(i => i.State is InstallState.Queued or InstallState.Running);

    partial void OnDeviceChanged(DeviceInfo? value)
    {
        OnPropertyChanged(nameof(DeviceTitle));
        OnPropertyChanged(nameof(DeviceSubtitle));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedCategoryChanged(string value) => ApplyFilter();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_api is null)
        {
            return;
        }

        if (!await _refreshGate.WaitAsync(0))
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ct = CancellationToken.None;
            var deviceTask = _api.GetDeviceAsync(ct);
            var catalogTask = _api.GetCatalogAsync(ct);
            var installsTask = _api.GetInstallsAsync(ct);
            await Task.WhenAll(deviceTask, catalogTask, installsTask);

            Device = deviceTask.Result;
            MergeCatalog(catalogTask.Result);
            ReplaceAll(Installs, installsTask.Result.OrderByDescending(i => i.RequestedAt));
            ReplaceAll(Activity, Installs.Select(i => new ActivityItemViewModel(i)));

            // Requests ride the same refresh as installs, so a decision an admin made shows up without
            // the user doing anything. A server from before M1-04 has no such route; that is not an error.
            try
            {
                ReplaceAll(Requests, (await _api.GetRequestsAsync(ct)).Select(r => new RequestItemViewModel(r)));
            }
            catch (PortalApiException)
            {
            }

            // Inventory is read after the install refresh so a just-finished install is already reflected in it.
            try
            {
                _installedRaw = await _api.GetInstalledAsync(ct);
                ReplaceAll(Installed, _installedRaw);
            }
            catch (PortalApiException ex)
            {
                // Inventory is best-effort: the catalog and history still render.
                ErrorMessage = "Installed software is unavailable right now. " + ex.Message;
            }

            ApplyStates();
            ErrorMessage = null;
            LastRefreshed = DateTimeOffset.Now;
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This runs from a timer tick, so anything that escapes here would reach the dispatcher
            // as an unhandled exception and end the process. Show it instead.
            ErrorMessage = "Something went wrong talking to the App Portal server. " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ActiveInstallCount));
            _refreshGate.Release();
        }
    }

    [RelayCommand]
    private async Task SubmitRequestAsync()
    {
        if (_api is null)
        {
            return;
        }

        var text = (RequestText ?? "").Trim();
        if (text.Length == 0)
        {
            RequestNotice = "Say what you would like installed.";
            return;
        }

        try
        {
            var created = await _api.CreateRequestAsync(text, CancellationToken.None);

            // Shown at once rather than waiting for the next poll, which is up to thirty seconds away.
            Requests.Insert(0, new RequestItemViewModel(created));
            RequestText = "";
            RequestNotice = "Sent. An administrator will answer it here.";
        }
        catch (PortalApiException ex)
        {
            RequestNotice = ex.Message;
        }
    }

    [RelayCommand]
    private void SelectSection(string index)
    {
        if (int.TryParse(index, out var i))
        {
            SelectedSection = i;
        }
    }

    /// <summary>Asks the agent to look for an update. It downloads and installs; this app only asks.</summary>
    [RelayCommand]
    private void UpdateNow()
    {
        if (IsDemo)
        {
            return;
        }

        UpdateRequested = UpdateStatusReader.RequestUpdate();
        if (!UpdateRequested)
        {
            ErrorMessage = AgentUnreachable;
        }
    }

    /// <summary>
    /// Asks the agent to update and then exits, because Windows Installer cannot replace files this
    /// process holds open.
    /// </summary>
    [RelayCommand]
    private async Task RestartToUpdateAsync()
    {
        if (IsDemo)
        {
            return;
        }

        if (!UpdateStatusReader.RequestUpdate())
        {
            ErrorMessage = AgentUnreachable;
            return;
        }

        await Task.Delay(300);
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void ReadUpdateStatus()
    {
        var status = UpdateStatusReader.Read();
        if (status != UpdateStatus)
        {
            UpdateStatus = status;
        }
    }

    private async Task TickAsync()
    {
        try
        {
            await TickCoreAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task TickCoreAsync()
    {
        ReadUpdateStatus();
        // Poll faster while something is installing; otherwise every few ticks is enough.
        if (IsDemo || ActiveInstallCount > 0 || LastRefreshed is null || DateTimeOffset.Now - LastRefreshed > TimeSpan.FromSeconds(60))
        {
            await RefreshAsync();
        }
    }

    /// <summary>
    /// Asks for the app to be taken off. The same shape as an install, because the server treats a
    /// removal as one: the same history row, the same states, the same progress on the card.
    /// </summary>
    private async Task RemoveAsync(AppItemViewModel item)
    {
        if (_api is null)
        {
            return;
        }

        item.LastError = null;
        item.IsRequesting = true;
        try
        {
            var request = await _api.RequestUninstallAsync(item.App.Id, CancellationToken.None);
            item.ActiveInstall = request;
            Installs.Insert(0, request);
            Activity.Insert(0, new ActivityItemViewModel(request));
            OnPropertyChanged(nameof(ActiveInstallCount));
            SelectedSection = 0;
        }
        catch (PortalApiException ex)
        {
            item.LastError = ex.Message;
        }
        finally
        {
            item.IsRequesting = false;
        }
    }

    private async Task InstallAsync(AppItemViewModel item)
    {
        if (_api is null)
        {
            return;
        }

        item.LastError = null;
        item.IsRequesting = true;
        try
        {
            var request = await _api.RequestInstallAsync(item.App.Id, CancellationToken.None);
            item.ActiveInstall = request;
            Installs.Insert(0, request);
            Activity.Insert(0, new ActivityItemViewModel(request));
            OnPropertyChanged(nameof(ActiveInstallCount));
            SelectedSection = 0;
        }
        catch (PortalApiException ex)
        {
            item.LastError = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            item.LastError = "The request could not be sent. " + ex.Message;
        }
        finally
        {
            item.IsRequesting = false;
        }
    }

    private void MergeCatalog(IReadOnlyList<CatalogApp> catalog)
    {
        var known = Apps.ToDictionary(a => a.App.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var app in catalog)
        {
            if (known.TryGetValue(app.Id, out var existing))
            {
                var iconChanged = existing.App.IconUrl != app.IconUrl;
                existing.App = app;
                if (iconChanged)
                {
                    existing.Icon = null;
                    _ = LoadIconAsync(existing);
                }
            }
            else
            {
                var item = new AppItemViewModel(app, InstallAsync, RemoveAsync);
                Apps.Add(item);
                if (app.IconUrl is not null)
                {
                    _ = LoadIconAsync(item);
                }
            }
        }

        foreach (var stale in Apps.Where(a => catalog.All(c => !string.Equals(c.Id, a.App.Id, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            Apps.Remove(stale);
        }

        var categories = new[] { "All" }.Concat(catalog.Select(c => c.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (!categories.SequenceEqual(Categories))
        {
            var selected = SelectedCategory;
            ReplaceAll(Categories, categories);
            SelectedCategory = categories.Contains(selected) ? selected : "All";
        }

        ApplyFilter();
    }

    private void ApplyStates()
    {
        foreach (var item in Apps)
        {
            var active = Installs.FirstOrDefault(i => string.Equals(i.AppId, item.App.Id, StringComparison.OrdinalIgnoreCase)
                                                      && i.State is InstallState.Queued or InstallState.Running);
            item.ActiveInstall = active;
            var installedByInventory = _installedRaw.Any(a => string.Equals(a.CatalogAppId, item.App.Id, StringComparison.OrdinalIgnoreCase));
            // A removal is a row in the same history with the same states, so the two have to be told
            // apart here. Without that, taking an app off leaves the card saying Installed and offering
            // Remove again until the inventory catches up, which is the opposite of what happened.
            var installedAt = LastSucceededAt(item.App.Id, InstallKind.Install);
            var removedAt = LastSucceededAt(item.App.Id, InstallKind.Uninstall);
            var justSucceeded = active is null && installedAt is { } done && DateTimeOffset.UtcNow - done < TimeSpan.FromHours(6);
            var justRemoved = active is null && removedAt is { } gone && DateTimeOffset.UtcNow - gone < TimeSpan.FromHours(6)
                              && (installedAt is not { } put || put < gone);
            item.IsInstalled = (installedByInventory || justSucceeded) && !justRemoved;
            if (active is null && item.LastError is null)
            {
                var lastFailure = Installs.FirstOrDefault(i => string.Equals(i.AppId, item.App.Id, StringComparison.OrdinalIgnoreCase) && i.State == InstallState.Failed);
                if (lastFailure is not null && !item.IsInstalled && lastFailure.CompletedAt is { } when && DateTimeOffset.UtcNow - when < TimeSpan.FromHours(1))
                {
                    item.LastError = "Last attempt failed. " + (lastFailure.Detail ?? "");
                }
            }
        }
    }

    /// <summary>When this app was last put on, or last taken off, whichever kind is asked for.</summary>
    private DateTimeOffset? LastSucceededAt(string appId, string kind)
        => Installs.Where(i => string.Equals(i.AppId, appId, StringComparison.OrdinalIgnoreCase)
                               && i.Kind == kind
                               && i.State == InstallState.Succeeded
                               && i.CompletedAt is not null)
            .Max(i => i.CompletedAt);

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        var filtered = Apps
            .Where(a => SelectedCategory == "All" || string.Equals(a.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase))
            .Where(a => text.Length == 0
                        || a.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || a.Publisher.Contains(text, StringComparison.OrdinalIgnoreCase)
                        || a.Description.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Featured)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ReplaceAll(FilteredApps, filtered);
    }

    private async Task LoadIconAsync(AppItemViewModel item)
    {
        var url = item.App.IconUrl;
        var bitmap = await _icons.GetAsync(url);
        if (bitmap is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // A slower download for the previous URL must not undo a newer catalog edit.
                if (item.App.IconUrl == url)
                {
                    item.Icon = bitmap;
                }
            });
        }
    }

    private static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
