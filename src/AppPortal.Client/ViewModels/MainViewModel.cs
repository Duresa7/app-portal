using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AppPortal.Client.Services;
using AppPortal.Shared;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IPortalApiClient? _api;
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private IReadOnlyList<InstalledApp> _installedRaw = [];

    public MainViewModel() : this(null, new ClientSettings())
    {
    }

    public MainViewModel(IPortalApiClient? api, ClientSettings settings)
    {
        _api = api;
        IsConfigured = api is not null && settings.IsConfigured;
        ConfigPath = ClientSettings.DefaultPath;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(Math.Max(3, settings.RefreshSeconds)), DispatcherPriority.Background, async (_, _) => await TickAsync());
        if (IsConfigured)
        {
            _timer.Start();
        }
    }

    public bool IsConfigured { get; }
    public string ConfigPath { get; }

    public ObservableCollection<AppItemViewModel> Apps { get; } = [];
    public ObservableCollection<AppItemViewModel> FilteredApps { get; } = [];
    public ObservableCollection<InstalledApp> Installed { get; } = [];
    public ObservableCollection<InstallRequest> Installs { get; } = [];
    public ObservableCollection<string> Categories { get; } = ["All"];

    [ObservableProperty] private DeviceInfo? _device;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private DateTimeOffset? _lastRefreshed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowApps), nameof(ShowInstalled), nameof(ShowActivity))]
    private int _selectedSection;

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _selectedCategory = "All";

    public bool ShowApps => SelectedSection == 0;
    public bool ShowInstalled => SelectedSection == 1;
    public bool ShowActivity => SelectedSection == 2;

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
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ActiveInstallCount));
            _refreshGate.Release();
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

    private async Task TickAsync()
    {
        // Poll faster while something is installing; otherwise every few ticks is enough.
        if (ActiveInstallCount > 0 || LastRefreshed is null || DateTimeOffset.Now - LastRefreshed > TimeSpan.FromSeconds(60))
        {
            await RefreshAsync();
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

    private void MergeCatalog(IReadOnlyList<CatalogApp> catalog)
    {
        var known = Apps.ToDictionary(a => a.App.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var app in catalog)
        {
            if (!known.ContainsKey(app.Id))
            {
                Apps.Add(new AppItemViewModel(app, InstallAsync));
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
            var justSucceeded = active is null && Installs.Any(i => string.Equals(i.AppId, item.App.Id, StringComparison.OrdinalIgnoreCase)
                                                                   && i.State == InstallState.Succeeded
                                                                   && i.CompletedAt is { } done && DateTimeOffset.UtcNow - done < TimeSpan.FromHours(6));
            item.IsInstalled = installedByInventory || justSucceeded;
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

    private static void ReplaceAll<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
