using AppPortal.Client.Services;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>A placeholder with its place in the navigation. M4-03 replaces it with the page itself.</summary>
public sealed class RequestsViewModel(IAdminApiClient api) : ComingSoonPageViewModel(
    api,
    "Requests",
    "Software people have asked for. Approve or deny each one, with a reason they will see.");
