using System;
using System.Collections.Generic;

using AppPortal.Client.Services;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>
/// The values of <see cref="MainViewModel.SelectedSection"/> that are admin pages. Ten and up, clear of
/// the user sections, so <c>--screenshot out.png 10</c> can name one and a new user section never
/// collides with them.
/// </summary>
public static class AdminSections
{
    public const int Dashboard = 10;
    public const int Installs = 11;
    public const int Catalog = 12;
    public const int Requests = 13;
    public const int Devices = 14;
    public const int Keys = 15;
    public const int Admins = 16;
    public const int Settings = 17;
}

/// <summary>
/// Every admin page, built once per sign-in. Signing out drops the whole thing, so nothing one
/// administrator loaded is still on screen for the next person to sign in on this PC.
/// </summary>
public sealed class AdminAreaViewModel : ViewModelBase
{
    public AdminAreaViewModel(IAdminApiClient api, Action<int> navigate)
    {
        Dashboard = new DashboardViewModel(api, navigate);
        Installs = new InstallsViewModel(api);
        Catalog = new CatalogViewModel(api);
        Requests = new RequestsViewModel(api);
        Devices = new DevicesViewModel(api);
        Keys = new KeysViewModel(api);
        Admins = new AdminsViewModel(api);
        Settings = new SettingsViewModel(api);
    }

    public DashboardViewModel Dashboard { get; }
    public InstallsViewModel Installs { get; }
    public CatalogViewModel Catalog { get; }
    public RequestsViewModel Requests { get; }
    public DevicesViewModel Devices { get; }
    public KeysViewModel Keys { get; }
    public AdminsViewModel Admins { get; }
    public SettingsViewModel Settings { get; }

    public IEnumerable<AdminPageViewModel> Pages => [Dashboard, Installs, Catalog, Requests, Devices, Keys, Admins, Settings];

    /// <summary>The page a section number shows, or null for a user section.</summary>
    public AdminPageViewModel? PageFor(int section) => section switch
    {
        AdminSections.Dashboard => Dashboard,
        AdminSections.Installs => Installs,
        AdminSections.Catalog => Catalog,
        AdminSections.Requests => Requests,
        AdminSections.Devices => Devices,
        AdminSections.Keys => Keys,
        AdminSections.Admins => Admins,
        AdminSections.Settings => Settings,
        _ => null,
    };
}
