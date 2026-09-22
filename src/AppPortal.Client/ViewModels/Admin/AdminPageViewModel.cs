using System.Threading.Tasks;

using AppPortal.Client.Services;

using CommunityToolkit.Mvvm.ComponentModel;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>
/// One page of the admin area. Each page is one of these and one view, and nothing else knows what is
/// on it, so a later package can fill a page by replacing its two files and touching no other page.
/// </summary>
public abstract partial class AdminPageViewModel(IAdminApiClient api) : ViewModelBase
{
    protected IAdminApiClient Api { get; } = api;

    /// <summary>Drives the accent indicator on this page's navigation item.</summary>
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string? _errorMessage;

    /// <summary>
    /// Called every time the page is shown, not only the first, so what it shows is as fresh as the
    /// last click. A page catches <see cref="PortalApiException"/> itself and shows the message; a
    /// refused session is handled above the page, by the session.
    /// </summary>
    public virtual Task ActivateAsync() => Task.CompletedTask;
}
