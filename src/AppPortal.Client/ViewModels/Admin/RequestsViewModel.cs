using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Client.Services;
using AppPortal.Shared;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>One tab over the request list. A null status is the All tab.</summary>
public sealed record RequestTab(string Label, AppRequestStatus? Status)
{
    public override string ToString() => Label;
}

/// <summary>
/// Software people have asked for, in the web page's four tabs, with the same two decisions. A decision
/// shows in the table the moment it is confirmed and is put back if the server refuses it, so deciding a
/// queue of requests is not a wait on every row.
/// </summary>
public sealed partial class RequestsViewModel : AdminPageViewModel
{
    /// <summary>The web page's page size.</summary>
    public const int PageSize = 50;

    /// <summary>The web page's tabs, in its order, opening on the queue somebody came to work through.</summary>
    public static readonly IReadOnlyList<RequestTab> Tabs =
    [
        new("Pending", AppRequestStatus.Pending),
        new("Approved", AppRequestStatus.Approved),
        new("Denied", AppRequestStatus.Denied),
        new("All", null),
    ];

    private int _offset;
    private int _loadVersion;

    public RequestsViewModel(IAdminApiClient api) : base(api)
    {
        _selectedTab = Tabs[0];
    }

    public ObservableCollection<RequestRowViewModel> Rows { get; } = [];

    /// <summary>The same list as <see cref="Tabs"/>, for the view to bind to.</summary>
    public IReadOnlyList<RequestTab> TabItems => Tabs;

    [ObservableProperty] private RequestTab _selectedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPager), nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    private bool _hasNext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPager), nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousCommand))]
    private bool _hasPrevious;

    /// <summary>For the badge on the navigation item, counted as the web counts it for its own badge.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending))]
    private int _pendingCount;

    /// <summary>The request the decision dialog is open for, or null when it is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDecisionOpen))]
    private RequestRowViewModel? _deciding;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecisionTitle), nameof(DecisionButtonText), nameof(IsApproving))]
    private AppRequestStatus _decision = AppRequestStatus.Approved;

    [ObservableProperty] private string _reason = "";

    /// <summary>What the last decision came to, in the web page's words. Cleared by the next one.</summary>
    [ObservableProperty] private string? _notice;

    public bool HasPending => PendingCount > 0;

    public bool HasPager => HasNext || HasPrevious;

    public string PageText => $"Page {(_offset / PageSize) + 1}";

    public bool HasRows => Rows.Count > 0;

    public bool IsDecisionOpen => Deciding is not null;

    public bool IsApproving => Decision == AppRequestStatus.Approved;

    public string DecisionTitle => IsApproving ? "Approve this request?" : "Deny this request?";

    public string DecisionButtonText => IsApproving ? "Approve" : "Deny";

    /// <summary>The server refuses anything longer, so the box stops the admin before the round trip does.</summary>
    public int ReasonMaxLength => AppRequestLimits.MaxTextLength;

    public override Task ActivateAsync() => LoadAsync();

    /// <summary>
    /// Asks the server how many requests are waiting. Called whenever an admin page is shown, as the web
    /// counts on every admin page it renders. A failure leaves the badge as it was: the badge is a hint,
    /// and the page that failed to load says why on its own.
    /// </summary>
    public async Task RefreshPendingCountAsync()
    {
        try
        {
            var page = await Api.GetRequestsAsync(AppRequestStatus.Pending, 0, 1, CancellationToken.None);
            PendingCount = page.Total ?? page.Items.Count;
        }
        catch (PortalApiException)
        {
        }
    }

    partial void OnSelectedTabChanged(RequestTab value)
    {
        _offset = 0;
        Notice = null;
        _ = LoadAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand(CanExecute = nameof(HasPrevious))]
    private Task PreviousAsync()
    {
        _offset = Math.Max(0, _offset - PageSize);
        return LoadAsync();
    }

    [RelayCommand(CanExecute = nameof(HasNext))]
    private Task NextAsync()
    {
        _offset += PageSize;
        return LoadAsync();
    }

    [RelayCommand]
    private void Approve(RequestRowViewModel? row) => OpenDecision(row, AppRequestStatus.Approved);

    [RelayCommand]
    private void Deny(RequestRowViewModel? row) => OpenDecision(row, AppRequestStatus.Denied);

    [RelayCommand]
    private void CancelDecision()
    {
        Deciding = null;
        Reason = "";
    }

    /// <summary>
    /// Closes the dialog, shows the decision on the row straight away, then asks the server. If the
    /// server says no, the row goes back to what it was and the reason is shown. If somebody else
    /// decided first, the list is read again so it shows the decision that was actually recorded.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmDecisionAsync()
    {
        if (Deciding is not { } row)
        {
            return;
        }

        var status = Decision;
        var reason = string.IsNullOrWhiteSpace(Reason) ? null : Reason.Trim();
        Deciding = null;
        Reason = "";

        var before = row.Request;
        var index = Rows.IndexOf(row);
        // On the Pending tab a decided request no longer belongs; the web table drops it the same way.
        var leaves = SelectedTab.Status is { } shown && shown != status;
        row.Request = before with { Status = status, Reason = reason, DecidedBy = null, DecidedAt = DateTimeOffset.Now };
        row.IsSaving = true;
        if (leaves)
        {
            Rows.Remove(row);
            OnPropertyChanged(nameof(HasRows));
        }

        PendingCount = Math.Max(0, PendingCount - 1);
        Notice = null;
        ErrorMessage = null;
        try
        {
            row.Request = status == AppRequestStatus.Approved
                ? await Api.ApproveRequestAsync(before.Id, reason, CancellationToken.None)
                : await Api.DenyRequestAsync(before.Id, reason, CancellationToken.None);
            Notice = status == AppRequestStatus.Approved ? "Request approved." : "Request denied.";
            // Counted again rather than trusted: another administrator may be working the same queue.
            await RefreshPendingCountAsync();
        }
        catch (PortalApiException ex)
        {
            row.Request = before;
            if (leaves && index >= 0)
            {
                Rows.Insert(Math.Min(index, Rows.Count), row);
                OnPropertyChanged(nameof(HasRows));
            }

            PendingCount++;
            if (ex.Status == HttpStatusCode.Conflict)
            {
                await LoadAsync();
                ErrorMessage = ex.Message + " The list now shows the decision that was recorded.";
            }
            else
            {
                ErrorMessage = ex.Message;
            }
        }
        finally
        {
            row.IsSaving = false;
        }
    }

    private void OpenDecision(RequestRowViewModel? row, AppRequestStatus decision)
    {
        if (row is null || !row.IsPending || row.IsSaving)
        {
            return;
        }

        Decision = decision;
        Reason = "";
        Deciding = row;
    }

    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        var tab = SelectedTab;
        var offset = _offset;
        IsBusy = true;
        try
        {
            var page = await Api.GetRequestsAsync(tab.Status, offset, PageSize, CancellationToken.None);
            if (version != _loadVersion)
            {
                return;
            }

            Rows.Clear();
            foreach (var request in page.Items)
            {
                Rows.Add(new RequestRowViewModel(request, Approve, Deny));
            }

            HasPrevious = offset > 0;
            HasNext = page.HasMore;
            OnPropertyChanged(nameof(PageText));
            OnPropertyChanged(nameof(HasRows));
            ErrorMessage = null;
        }
        catch (PortalApiException ex)
        {
            if (version == _loadVersion)
            {
                ErrorMessage = ex.Message;
            }

            return;
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsBusy = false;
            }
        }

        await RefreshPendingCountAsync();
    }
}

/// <summary>One request in the table, worded as the web page words it.</summary>
public sealed partial class RequestRowViewModel : ObservableObject
{
    public RequestRowViewModel(AdminRequest request, Action<RequestRowViewModel?> approve, Action<RequestRowViewModel?> deny)
    {
        _request = request;
        ApproveCommand = new RelayCommand(() => approve(this));
        DenyCommand = new RelayCommand(() => deny(this));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubmittedText), nameof(DeviceName), nameof(RequesterText), nameof(Text), nameof(IsPending),
        nameof(IsApproved), nameof(IsDenied), nameof(StatusText), nameof(ReasonText), nameof(DecidedText), nameof(CanDecide))]
    private AdminRequest _request;

    /// <summary>True between a decision on screen and the server's answer to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DecidedText), nameof(CanDecide))]
    private bool _isSaving;

    public IRelayCommand ApproveCommand { get; }

    public IRelayCommand DenyCommand { get; }

    public string SubmittedText => When(Request.CreatedAt);
    public string DeviceName => Request.DeviceName;
    public string RequesterText => string.IsNullOrWhiteSpace(Request.RequestedBy) ? "unknown" : Request.RequestedBy;
    public string Text => Request.Text;
    public bool IsPending => Request.Status == AppRequestStatus.Pending;
    public bool IsApproved => Request.Status == AppRequestStatus.Approved;
    public bool IsDenied => Request.Status == AppRequestStatus.Denied;
    public string StatusText => Request.Status.ToString();
    public string ReasonText => string.IsNullOrWhiteSpace(Request.Reason) ? "No reason given." : Request.Reason;

    public string DecidedText
    {
        get
        {
            if (IsSaving)
            {
                return "Saving…";
            }

            var on = Request.DecidedAt is { } at ? " on " + When(at) : "";
            return $"by {Request.DecidedBy ?? "unknown"}{on}";
        }
    }

    public bool CanDecide => IsPending && !IsSaving;

    private static string When(DateTimeOffset at) => at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
