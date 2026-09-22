using AppPortal.Client.Services;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>A placeholder with its place in the navigation. M4-05 replaces it with the page itself.</summary>
public sealed class SettingsViewModel(IAdminApiClient api) : ComingSoonPageViewModel(
    api,
    "Settings",
    "The install engine the server prefers when a device has both.");
