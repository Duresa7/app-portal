using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Client.Services;
using AppPortal.Shared;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>One device as the fleet table shows it.</summary>
public sealed record DeviceRow(AdminDevice Device)
{
    public string Name => Device.Name;

    /// <summary>A device has Action1 when it has an endpoint to deploy to, the same test the server makes.</summary>
    public bool HasAction1 => !string.IsNullOrWhiteSpace(Device.EndpointId);

    public bool HasAgent => Device.HasAgent;

    public bool HasNoEngine => !HasAction1 && !HasAgent;

    public string AgentVersionText => Device.AgentVersion ?? "—";

    public string LastSeenText => Device.LastSeenAt is { } seen ? seen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "never";

    public string StatusText => Device.Enabled ? "Enabled" : "Disabled";

    public string EnrolledWithText => Device.EnrolledWithKeyId is null ? "added by hand" : Device.EnrolledWithKeyName ?? Device.EnrolledWithKeyId;

    public string InstallCountText => Device.InstallCount.ToString("N0", CultureInfo.CurrentCulture);

    public string RegisteredText => Device.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string AgentText => Device.HasAgent ? Device.AgentVersion ?? "enrolled" : "not enrolled";
}

/// <summary>One package manager the agent found on the device.</summary>
public sealed record DeviceManagerRow(DeviceManager Manager)
{
    public string NameText => PackageManagers.Find(Manager.Name)?.DisplayName ?? Manager.Name;

    public string VersionText => string.IsNullOrEmpty(Manager.Version) ? "not known" : Manager.Version;

    public string ForText => Manager.Account ?? "everyone on this PC";
}

/// <summary>One of the device's recent installs.</summary>
public sealed record DeviceInstallRow(AdminInstall Install)
{
    public string RequestedText => Install.RequestedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string AppName => Install.AppName;

    public string RequestedByText => string.IsNullOrWhiteSpace(Install.RequestedBy) ? "—" : Install.RequestedBy;

    public string StateText => Install.State.ToString();

    public bool Failed => Install.State == InstallState.Failed;
}

/// <summary>One of the software requests made from the device.</summary>
public sealed record DeviceRequestRow(AdminRequest Request)
{
    public string CreatedText => Request.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string Text => Request.Text;

    public string RequestedByText => string.IsNullOrWhiteSpace(Request.RequestedBy) ? "—" : Request.RequestedBy;

    public string StatusText => Request.Status.ToString();
}

/// <summary>
/// The fleet, as the web device pages work it: the table with search, one device with its history,
/// its settings, a rotated token shown once, removal, and adding a device by hand.
/// </summary>
public sealed partial class DevicesViewModel(IAdminApiClient api) : AdminPageViewModel(api)
{
    private int _loaded;

    /// <summary>The search the table shows, which Load more continues even if the box has changed since.</summary>
    private string _searched = "";

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public ObservableCollection<DeviceManagerRow> Managers { get; } = [];

    public ObservableCollection<DeviceInstallRow> RecentInstalls { get; } = [];

    public ObservableCollection<DeviceRequestRow> RecentRequests { get; } = [];

    public ShowOnceViewModel ShowOnce { get; } = new();

    /// <summary>What the engine preference offers. The empty value means the device follows the server.</summary>
    public IReadOnlyList<SettingsViewModel.EngineChoice> EnginePreferences { get; } =
    [
        new("", "Follow the server"),
        new(EngineLabel.Action1, "Action1"),
        new(EngineLabel.Agent, "Agent"),
    ];

    [ObservableProperty] private string _search = "";

    [ObservableProperty] private bool _hasMore;

    [ObservableProperty] private string? _notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen), nameof(IsListOpen), nameof(SelectedRow))]
    private AdminDevice? _selected;

    [ObservableProperty] private string _editName = "";

    [ObservableProperty] private string _editEndpointId = "";

    [ObservableProperty] private bool _editEnabled;

    [ObservableProperty] private SettingsViewModel.EngineChoice? _editEnginePreference;

    [ObservableProperty] private bool _isAddOpen;

    [ObservableProperty] private string _newName = "";

    [ObservableProperty] private string _newEndpointId = "";

    [ObservableProperty] private string? _addError;

    [ObservableProperty] private bool _isRotateOpen;

    [ObservableProperty] private bool _isRemoveOpen;

    public bool IsDetailOpen => Selected is not null;

    public bool IsListOpen => Selected is null;

    /// <summary>The open device in the same shape as a table row, for the text the two share.</summary>
    public DeviceRow? SelectedRow => Selected is null ? null : new DeviceRow(Selected);

    public override async Task ActivateAsync()
    {
        IsBusy = true;
        try
        {
            await LoadListAsync();
            if (Selected is { } open)
            {
                await LoadDeviceAsync(open.Id);
            }

            ErrorMessage = null;
        }
        catch (PortalApiException ex) when (ex.Status == HttpStatusCode.NotFound && Selected is not null)
        {
            // Removed elsewhere, on the web or by another administrator, while this page was open on it.
            Selected = null;
            ClearDetail();
            ErrorMessage = ex.Message;
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        Notice = null;
        return ActivateAsync();
    }

    [RelayCommand]
    private async Task SearchDevicesAsync()
    {
        IsBusy = true;
        try
        {
            await LoadListAsync();
            ErrorMessage = null;
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        IsBusy = true;
        try
        {
            Append(await Api.GetDevicesAsync(_searched, _loaded, AdminApiLimits.DefaultLimit, CancellationToken.None));
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task OpenDeviceAsync(DeviceRow? row)
    {
        if (row is null)
        {
            return;
        }

        Notice = null;
        ClearDetail();
        Show(row.Device);
        IsBusy = true;
        try
        {
            await LoadDeviceAsync(row.Device.Id);
            ErrorMessage = null;
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task BackAsync()
    {
        Selected = null;
        ClearDetail();
        ErrorMessage = null;
        // The table may be out of date after a rename or a disable, so it is read again on the way back.
        await SearchDevicesAsync();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Selected is not { } device)
        {
            return;
        }

        IsBusy = true;
        Notice = null;
        try
        {
            var saved = await Api.UpdateDeviceAsync(
                device.Id,
                new AdminDeviceUpdate(EditName.Trim(), EditEndpointId.Trim(), EditEnabled, NullIfEmpty(EditEnginePreference?.Value)),
                CancellationToken.None);
            Show(saved);
            ErrorMessage = null;
            Notice = "Saved.";
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RequestRotate() => IsRotateOpen = Selected is not null;

    [RelayCommand]
    private void CancelRotate() => IsRotateOpen = false;

    [RelayCommand]
    private async Task ConfirmRotateAsync()
    {
        IsRotateOpen = false;
        if (Selected is not { } device)
        {
            return;
        }

        IsBusy = true;
        Notice = null;
        try
        {
            var issued = await Api.RotateDeviceTokenAsync(device.Id, CancellationToken.None);
            ErrorMessage = null;
            Notice = "A new token is issued. The old one stopped working just now, so the device cannot call in until it has this one.";
            ShowOnce.Show(
                $"New token for {device.Name}",
                "Shown once and not stored. Put it in the device's client.json now, or enroll the device again with an enrollment key.",
                issued.DeviceToken);
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RequestRemove() => IsRemoveOpen = Selected is not null;

    [RelayCommand]
    private void CancelRemove() => IsRemoveOpen = false;

    [RelayCommand]
    private async Task ConfirmRemoveAsync()
    {
        IsRemoveOpen = false;
        if (Selected is not { } device)
        {
            return;
        }

        IsBusy = true;
        Notice = null;
        try
        {
            await Api.DeleteDeviceAsync(device.Id, CancellationToken.None);
        }
        catch (PortalApiException ex)
        {
            // A device with an install still running is refused; the server says so and what to do instead.
            ErrorMessage = ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await BackAsync();
        Notice = $"'{device.Name}' is removed. Its install and request history stays under that name.";
    }

    [RelayCommand]
    private void OpenAdd()
    {
        NewName = "";
        NewEndpointId = "";
        AddError = null;
        IsAddOpen = true;
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsAddOpen = false;
        AddError = null;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var name = NewName.Trim();
        if (name.Length == 0)
        {
            AddError = "A device needs a name.";
            return;
        }

        IsBusy = true;
        AddError = null;
        try
        {
            var endpoint = NewEndpointId.Trim();
            var issued = await Api.CreateDeviceAsync(new AdminDeviceCreate(name, endpoint.Length == 0 ? null : endpoint), CancellationToken.None);
            IsAddOpen = false;
            Notice = $"'{name}' is registered.";
            ShowOnce.Show(
                $"Token for {name}",
                "This is shown once and is not stored. Put it in the device's client.json now. If it is lost, rotate the token from the device's page.",
                issued.DeviceToken);
        }
        catch (PortalApiException ex)
        {
            AddError = ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await SearchDevicesAsync();
    }

    private async Task LoadListAsync()
    {
        _searched = Search.Trim();
        var page = await Api.GetDevicesAsync(_searched, 0, AdminApiLimits.DefaultLimit, CancellationToken.None);
        Devices.Clear();
        Append(page);
    }

    private async Task LoadDeviceAsync(string id)
    {
        var detail = await Api.GetDeviceAsync(id, CancellationToken.None);
        Show(detail.Device);
        Replace(Managers, (detail.Managers ?? []).Select(m => new DeviceManagerRow(m)));
        Replace(RecentInstalls, detail.RecentInstalls.Select(i => new DeviceInstallRow(i)));
        Replace(RecentRequests, detail.RecentRequests.Select(r => new DeviceRequestRow(r)));
    }

    /// <summary>The device and its settings form, filled from what the server holds now.</summary>
    private void Show(AdminDevice device)
    {
        Selected = device;
        EditName = device.Name;
        EditEndpointId = device.EndpointId;
        EditEnabled = device.Enabled;
        EditEnginePreference = EnginePreferences.FirstOrDefault(e => e.Value == (device.EnginePreference ?? "")) ?? EnginePreferences[0];
    }

    private void ClearDetail()
    {
        Managers.Clear();
        RecentInstalls.Clear();
        RecentRequests.Clear();
        IsRotateOpen = false;
        IsRemoveOpen = false;
    }

    private void Append(AdminPage<AdminDevice> page)
    {
        foreach (var device in page.Items)
        {
            Devices.Add(new DeviceRow(device));
        }

        _loaded = page.Offset + page.Items.Count;
        HasMore = page.HasMore;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(row);
        }
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
