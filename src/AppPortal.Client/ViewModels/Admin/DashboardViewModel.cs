using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Client.Services;
using AppPortal.Shared;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>The five tiles of the web dashboard, from the same counts, each opening the page behind it.</summary>
public sealed partial class DashboardViewModel(IAdminApiClient api, Action<int> navigate) : AdminPageViewModel(api)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DevicesText), nameof(InstallsTodayText), nameof(FailuresThisWeekText),
        nameof(ActiveNowText), nameof(PendingRequestsText), nameof(HasFailures))]
    private DashboardCounts? _counts;

    public string DevicesText => Number(Counts?.Devices);
    public string InstallsTodayText => Number(Counts?.InstallsToday);
    public string FailuresThisWeekText => Number(Counts?.FailuresThisWeek);
    public string ActiveNowText => Number(Counts?.ActiveNow);
    public string PendingRequestsText => Number(Counts?.PendingRequests);

    /// <summary>The web page turns this one number red when it is not zero; so does this.</summary>
    public bool HasFailures => Counts?.FailuresThisWeek > 0;

    public override async Task ActivateAsync()
    {
        IsBusy = true;
        try
        {
            Counts = await Api.GetDashboardAsync(CancellationToken.None);
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
    private Task RefreshAsync() => ActivateAsync();

    [RelayCommand]
    private void OpenDevices() => navigate(AdminSections.Devices);

    [RelayCommand]
    private void OpenInstalls() => navigate(AdminSections.Installs);

    [RelayCommand]
    private void OpenRequests() => navigate(AdminSections.Requests);

    /// <summary>A dash until the first answer, so a tile never claims a zero nobody counted.</summary>
    private static string Number(int? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? "–";
}
