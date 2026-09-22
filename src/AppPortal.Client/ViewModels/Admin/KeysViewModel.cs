using AppPortal.Client.Services;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>A placeholder with its place in the navigation. M4-05 replaces it with the page itself.</summary>
public sealed class KeysViewModel(IAdminApiClient api) : ComingSoonPageViewModel(
    api,
    "Enrollment keys",
    "The keys new PCs enroll with. Create one, see which PCs used it, revoke it.");
