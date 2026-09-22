using AppPortal.Client.Services;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>A placeholder with its place in the navigation. M4-03 replaces it with the page itself.</summary>
public sealed class InstallsViewModel(IAdminApiClient api) : ComingSoonPageViewModel(
    api,
    "Installs",
    "Every install and removal across the fleet, with the same filters as the web page.");
