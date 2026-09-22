using AppPortal.Client.Services;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>A placeholder with its place in the navigation. M4-04 replaces it with the page itself.</summary>
public sealed class CatalogViewModel(IAdminApiClient api) : ComingSoonPageViewModel(
    api,
    "Catalog",
    "Add, edit and hide the apps devices can install, from every source the web form accepts.");
