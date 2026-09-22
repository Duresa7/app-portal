using AppPortal.Client.Services;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>A placeholder with its place in the navigation. M4-05 replaces it with the page itself.</summary>
public sealed class DevicesViewModel(IAdminApiClient api) : ComingSoonPageViewModel(
    api,
    "Devices",
    "Every enrolled PC: rename it, choose its install engine, rotate its token or remove it.");
