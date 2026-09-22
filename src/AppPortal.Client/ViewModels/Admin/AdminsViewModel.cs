using AppPortal.Client.Services;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>A placeholder with its place in the navigation. M4-05 replaces it with the page itself.</summary>
public sealed class AdminsViewModel(IAdminApiClient api) : ComingSoonPageViewModel(
    api,
    "Admins",
    "The accounts that can sign in here and on the web admin pages.");
