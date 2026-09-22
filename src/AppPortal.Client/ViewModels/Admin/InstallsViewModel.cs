using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Client.Services;
using AppPortal.Shared;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>
/// One choice in a filter box. <see cref="Value"/> is what goes to the server; null is "Any", which
/// sends nothing, so the box and the web page's empty option mean the same thing.
/// </summary>
public sealed record FilterOption(string Label, string? Value)
{
    public static readonly FilterOption Any = new("Any", null);

    public override string ToString() => Label;
}

/// <summary>
/// The fleet install history, with the web page's columns, filters and pager, and the detail page as a
/// flyout beside the table. The rows reload every thirty seconds while the page is on screen, as the
/// web table does, so a running install is seen to finish without anybody clicking.
/// </summary>
public sealed partial class InstallsViewModel : AdminPageViewModel
{
    /// <summary>The web page's page size, so "Showing 1 to 100 of 340" reads the same in both.</summary>
    public const int PageSize = 100;

    public static readonly TimeSpan DefaultRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _refreshInterval;
    private CancellationTokenSource? _autoRefresh;

    /// <summary>What the rows show, which is not what the boxes hold until somebody presses Filter.</summary>
    private AdminInstallFilter _applied = new();

    private int _offset;

    /// <summary>Counts loads, so an answer that arrives after a newer question was asked is dropped.</summary>
    private int _loadVersion;

    public InstallsViewModel(IAdminApiClient api) : this(api, DefaultRefreshInterval)
    {
    }

    /// <param name="refreshInterval">Thirty seconds in the app; shorter in tests.</param>
    public InstallsViewModel(IAdminApiClient api, TimeSpan refreshInterval) : base(api)
    {
        _refreshInterval = refreshInterval;
        StateOptions = [FilterOption.Any, .. Enum.GetValues<InstallState>().Select(s => new FilterOption(s.ToString(), s.ToString()))];
    }

    public ObservableCollection<InstallRowViewModel> Rows { get; } = [];

    public ObservableCollection<FilterOption> DeviceOptions { get; } = [FilterOption.Any];

    public ObservableCollection<FilterOption> AppOptions { get; } = [FilterOption.Any];

    public IReadOnlyList<FilterOption> StateOptions { get; }

    [ObservableProperty] private FilterOption _selectedDevice = FilterOption.Any;
    [ObservableProperty] private FilterOption _selectedApp = FilterOption.Any;
    [ObservableProperty] private FilterOption _selectedState = FilterOption.Any;
    [ObservableProperty] private string _requester = "";
    [ObservableProperty] private DateTime? _from;
    [ObservableProperty] private DateTime? _to;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PagerText), nameof(HasRows), nameof(HasPager))]
    private int? _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPager))]
    [NotifyCanExecuteChangedFor(nameof(OlderCommand))]
    private bool _hasOlder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPager))]
    [NotifyCanExecuteChangedFor(nameof(NewerCommand))]
    private bool _hasNewer;

    /// <summary>The row the flyout shows. Choosing a row opens it; closing it clears this.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen))]
    private InstallRowViewModel? _selectedRow;

    /// <summary>Stopping asks first, as the web page's confirm box does.</summary>
    [ObservableProperty] private bool _isConfirmingStop;

    [ObservableProperty] private bool _isStopping;

    /// <summary>A success to report, such as a stopped install. Cleared by the next action.</summary>
    [ObservableProperty] private string? _notice;

    public bool IsDetailOpen => SelectedRow is not null;

    public bool HasRows => Rows.Count > 0;

    public bool HasPager => Total > 0 || HasNewer || HasOlder;

    /// <summary>The web pager's own words.</summary>
    public string PagerText => Rows.Count == 0
        ? ""
        : Total is { } total
            ? $"Showing {_offset + 1} to {_offset + Rows.Count} of {total}"
            : $"Showing {_offset + 1} to {_offset + Rows.Count}";

    /// <summary>True while the thirty-second reload is armed. For tests; the view does not show it.</summary>
    public bool IsAutoRefreshing => _autoRefresh is not null;

    public override async Task ActivateAsync()
    {
        StartAutoRefresh();
        // The rows first, because they are what the page was opened for; the filter choices after.
        await LoadRowsAsync(quiet: false);
        await LoadOptionsAsync();
    }

    /// <summary>
    /// Reloads the rows on screen with the filter and page they were loaded with. The timer calls this;
    /// so does the Refresh button.
    /// </summary>
    public Task ReloadAsync() => LoadRowsAsync(quiet: true);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // Another page was chosen, or the admin area closed: nobody is looking, so nothing is polled.
        if (e.PropertyName == nameof(IsSelected) && !IsSelected)
        {
            StopAutoRefresh();
        }
    }

    partial void OnSelectedRowChanged(InstallRowViewModel? value)
    {
        IsConfirmingStop = false;
        Notice = null;
        if (value is not null)
        {
            _ = LoadDetailAsync(value);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => ActivateAsync();

    [RelayCommand]
    private Task ApplyFilterAsync()
    {
        _applied = new AdminInstallFilter(
            SelectedDevice.Value,
            SelectedApp.Value,
            Enum.TryParse<InstallState>(SelectedState.Value, out var state) ? state : null,
            string.IsNullOrWhiteSpace(Requester) ? null : Requester.Trim(),
            From is { } from ? DateOnly.FromDateTime(from) : null,
            To is { } to ? DateOnly.FromDateTime(to) : null);
        _offset = 0;
        SelectedRow = null;
        return LoadRowsAsync(quiet: false);
    }

    [RelayCommand]
    private Task ClearFiltersAsync()
    {
        SelectedDevice = FilterOption.Any;
        SelectedApp = FilterOption.Any;
        SelectedState = FilterOption.Any;
        Requester = "";
        From = null;
        To = null;
        return ApplyFilterAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNewer))]
    private Task NewerAsync()
    {
        _offset = Math.Max(0, _offset - PageSize);
        SelectedRow = null;
        return LoadRowsAsync(quiet: false);
    }

    [RelayCommand(CanExecute = nameof(HasOlder))]
    private Task OlderAsync()
    {
        _offset += Rows.Count;
        SelectedRow = null;
        return LoadRowsAsync(quiet: false);
    }

    [RelayCommand]
    private void CloseDetail() => SelectedRow = null;

    [RelayCommand]
    private void AskToStop()
    {
        Notice = null;
        IsConfirmingStop = true;
    }

    [RelayCommand]
    private void KeepRunning() => IsConfirmingStop = false;

    [RelayCommand]
    private async Task StopAsync()
    {
        var row = SelectedRow;
        IsConfirmingStop = false;
        if (row is null || !row.CanStop)
        {
            return;
        }

        IsStopping = true;
        Notice = null;
        try
        {
            row.Install = await Api.CancelInstallAsync(row.Install.Id, CancellationToken.None);
            ErrorMessage = null;
            Notice = "The install was stopped.";
        }
        catch (PortalApiException ex)
        {
            // The server says why, and in a way the admin can act on: it finished first, or Action1 owns it.
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsStopping = false;
        }
    }

    /// <summary>The web detail page's "Everything on this device": the same filter, one click away.</summary>
    [RelayCommand]
    private Task ShowDeviceInstallsAsync()
    {
        if (SelectedRow is not { } row)
        {
            return Task.CompletedTask;
        }

        SelectedDevice = DeviceOption(row.DeviceName);
        SelectedApp = FilterOption.Any;
        SelectedState = FilterOption.Any;
        Requester = "";
        From = null;
        To = null;
        return ApplyFilterAsync();
    }

    private void StartAutoRefresh()
    {
        if (_autoRefresh is not null)
        {
            return;
        }

        _autoRefresh = new CancellationTokenSource();
        _ = AutoRefreshAsync(_autoRefresh.Token);
    }

    private void StopAutoRefresh()
    {
        _autoRefresh?.Cancel();
        _autoRefresh?.Dispose();
        _autoRefresh = null;
    }

    private async Task AutoRefreshAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // Resumes on the thread that started it, which in the app is the UI thread, so the rows
                // are only ever changed from there.
                await Task.Delay(_refreshInterval, ct);
                await ReloadAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Nothing awaits this loop, so whatever it does not catch would vanish unseen.
            ErrorMessage = "Something went wrong talking to the App Portal server. " + ex.Message;
        }
    }

    /// <param name="quiet">
    /// A reload of the same slice keeps the row objects, so the selection and the flyout stay put while
    /// the percentages move. A new filter or page starts over.
    /// </param>
    private async Task LoadRowsAsync(bool quiet)
    {
        var version = ++_loadVersion;
        var filter = _applied;
        var offset = _offset;
        IsBusy = !quiet;
        if (!quiet)
        {
            Notice = null;
        }

        try
        {
            var page = await Api.GetInstallsAsync(filter, offset, PageSize, CancellationToken.None);
            if (version != _loadVersion)
            {
                return;
            }

            Merge(page.Items, keepRows: quiet);
            Total = page.Total;
            HasOlder = page.HasMore;
            HasNewer = offset > 0;
            ErrorMessage = null;
        }
        catch (PortalApiException ex)
        {
            if (version == _loadVersion)
            {
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsBusy = false;
            }

            OnPropertyChanged(nameof(PagerText));
            OnPropertyChanged(nameof(HasRows));
        }
    }

    private void Merge(IReadOnlyList<AdminInstall> installs, bool keepRows)
    {
        if (keepRows && installs.Select(i => i.Id).SequenceEqual(Rows.Select(r => r.Install.Id)))
        {
            for (var i = 0; i < installs.Count; i++)
            {
                Rows[i].Install = installs[i];
            }

            return;
        }

        var selected = SelectedRow?.Install.Id;
        var rows = installs.Select(i => new InstallRowViewModel(i)).ToList();
        Rows.Clear();
        foreach (var row in rows)
        {
            Rows.Add(row);
        }

        // A new install arrived at the top: the one being read stays open if it is still on this page.
        if (keepRows && selected is not null)
        {
            SelectedRow = rows.FirstOrDefault(r => r.Install.Id == selected);
        }
    }

    /// <summary>The flyout opens on what the row already knows and then asks the server for the latest.</summary>
    private async Task LoadDetailAsync(InstallRowViewModel row)
    {
        try
        {
            var fresh = await Api.GetInstallAsync(row.Install.Id, CancellationToken.None);
            row.Install = fresh;
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// The choices for the device and app boxes, from the same lists the web page reads. A failure here
    /// leaves the boxes as they were and says so; the table can still be read.
    /// </summary>
    private async Task LoadOptionsAsync()
    {
        try
        {
            var devices = await AllAsync((offset, limit) => Api.GetDevicesAsync(null, offset, limit, CancellationToken.None));
            var apps = await AllAsync((offset, limit) => Api.GetCatalogAsync(null, offset, limit, CancellationToken.None));

            var device = SelectedDevice;
            var app = SelectedApp;
            Refill(DeviceOptions, devices.Select(d => d.Name).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Select(n => new FilterOption(n, n)));
            Refill(AppOptions, apps.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(a => new FilterOption(a.Name, a.Id)));

            // The same choice again, found by value: the option objects are new, and a box whose item is
            // no longer in its list shows nothing.
            SelectedDevice = device.Value is null ? FilterOption.Any : DeviceOption(device.Value);
            SelectedApp = AppOptions.FirstOrDefault(o => string.Equals(o.Value, app.Value, StringComparison.OrdinalIgnoreCase)) ?? FilterOption.Any;
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// The option for a device name, added when the list lacks it: a device deleted since its installs
    /// ran still has history, and "Everything on this device" must still be able to show it.
    /// </summary>
    private FilterOption DeviceOption(string name)
    {
        var option = DeviceOptions.FirstOrDefault(o => string.Equals(o.Value, name, StringComparison.OrdinalIgnoreCase));
        if (option is null)
        {
            option = new FilterOption(name, name);
            DeviceOptions.Add(option);
        }

        return option;
    }

    private static void Refill(ObservableCollection<FilterOption> options, IEnumerable<FilterOption> items)
    {
        var list = items.ToList();
        options.Clear();
        options.Add(FilterOption.Any);
        foreach (var item in list)
        {
            options.Add(item);
        }
    }

    /// <summary>Every row of a paged list. A fleet has more devices than one call returns.</summary>
    private static async Task<List<T>> AllAsync<T>(Func<int, int, Task<AdminPage<T>>> read)
    {
        var all = new List<T>();
        while (true)
        {
            var page = await read(all.Count, AdminApiLimits.MaxLimit);
            all.AddRange(page.Items);
            if (!page.HasMore || page.Items.Count == 0)
            {
                return all;
            }
        }
    }
}

/// <summary>One install in the table and in the flyout, worded as the web page words it.</summary>
public sealed partial class InstallRowViewModel(AdminInstall install) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequestedText), nameof(DeviceName), nameof(RequesterText), nameof(AppName), nameof(AppId),
        nameof(EngineText), nameof(KindText), nameof(StateText), nameof(DetailText), nameof(HasDetail), nameof(CompletedText),
        nameof(LastCheckedText), nameof(EndpointText), nameof(ReferenceText), nameof(StepText), nameof(HasStep),
        nameof(IsActive), nameof(IsSucceeded), nameof(IsFailed), nameof(IsCancelled), nameof(CanStop), nameof(StopUnavailableText))]
    private AdminInstall _install = install;

    public string RequestedText => When(Install.RequestedAt);
    public string DeviceName => Install.DeviceName;
    public string RequesterText => Or(Install.RequestedBy);
    public string AppName => Install.AppName;
    public string AppId => Install.AppId;
    public string EngineText => Or(EngineLabel.For(Install.Engine));
    public string KindText => Install.Kind == InstallKind.Uninstall ? "Removal" : "Install";

    /// <summary>The state, with the percentage while it is still going, as the web pill and its note show.</summary>
    public string StateText => IsActive
        ? $"{Install.State} {Install.PercentComplete.ToString(CultureInfo.CurrentCulture)}%"
        : Install.State.ToString();

    public string DetailText => Install.Detail ?? "";
    public bool HasDetail => !string.IsNullOrWhiteSpace(Install.Detail);
    public string CompletedText => Install.CompletedAt is { } done ? When(done) : "—";
    public string LastCheckedText => Install.LastCheckedAt is { } at ? When(at) : "—";
    public string EndpointText => Or(Install.EndpointId);
    public string ReferenceText => Or(Install.AutomationId);

    public string StepText => Install.StepCount > 0
        ? $"Step {Install.StepNumber} of {Install.StepCount}" + (string.IsNullOrWhiteSpace(Install.StepName) ? "" : $": {Install.StepName}")
        : "";

    public bool HasStep => StepText.Length > 0;

    public bool IsActive => Install.State is InstallState.Queued or InstallState.Running;
    public bool IsSucceeded => Install.State == InstallState.Succeeded;
    public bool IsFailed => Install.State == InstallState.Failed;
    public bool IsCancelled => Install.State == InstallState.Cancelled;

    /// <summary>
    /// Only what the agent is carrying out can be stopped from here. Action1 owns what Action1 started,
    /// so the server refuses the rest, and the web page does not offer the button for them either.
    /// </summary>
    public bool CanStop => IsActive && string.Equals(Install.Engine, EngineLabel.Agent, StringComparison.OrdinalIgnoreCase);

    /// <summary>Why a running install has no Stop button, rather than the button simply missing.</summary>
    public string StopUnavailableText => IsActive && !CanStop ? "Action1 is carrying out this install, so it has to be stopped in Action1." : "";

    private static string When(DateTimeOffset at) => at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
