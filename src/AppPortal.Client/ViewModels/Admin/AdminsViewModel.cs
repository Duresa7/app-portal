using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Client.Services;
using AppPortal.Shared;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>One administrator account as the table shows it.</summary>
public sealed record AdminAccountRow(AdminAccount Account, bool IsYou)
{
    public string Username => Account.Username;

    public bool IsDirectory => string.Equals(Account.Source, "directory", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the password lives, which is the web table's second column.</summary>
    public string PasswordText => IsDirectory ? "Directory" : "Local";

    public string StatusText => Account.Disabled ? "Disabled" : "Enabled";

    public string LastSignedInText => Account.LastLoginAt is { } at
        ? at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "never";

    /// <summary>Not for your own account, and not twice: the web offers no Enable, so neither does this.</summary>
    public bool CanDisable => !Account.Disabled && !IsYou;

    /// <summary>A directory password lives in the directory; the server refuses to set one here.</summary>
    public bool CanResetPassword => !IsDirectory;
}

/// <summary>
/// The administrator accounts, as the web administrators page works them: list, add, disable, reset a
/// password. Passwords typed here go to the server in the one call that needs them and are cleared from
/// the form straight after, whichever way the call went.
/// </summary>
public sealed partial class AdminsViewModel(IAdminApiClient api, string? currentUsername = null) : AdminPageViewModel(api)
{
    /// <summary>The server's own minimum, repeated so the form can say it before anything is sent.</summary>
    public const int MinimumPasswordLength = 12;

    /// <summary>Long enough to be strong, from letters and digits nobody misreads when typing it on another PC.</summary>
    private const int GeneratedPasswordLength = 20;

    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private int _loaded;

    public ObservableCollection<AdminAccountRow> Accounts { get; } = [];

    public ShowOnceViewModel ShowOnce { get; } = new();

    public string PasswordRule => $"At least {MinimumPasswordLength} characters.";

    [ObservableProperty] private bool _hasMore;

    [ObservableProperty] private string? _notice;

    [ObservableProperty] private bool _isAddOpen;

    [ObservableProperty] private string _newUsername = "";

    [ObservableProperty] private string _newPassword = "";

    /// <summary>On by default: a generated password is shown once and is stronger than most typed ones.</summary>
    [ObservableProperty] private bool _generateNewPassword = true;

    [ObservableProperty] private string? _addError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsResetOpen), nameof(ResetIsYou))]
    private AdminAccountRow? _resetTarget;

    [ObservableProperty] private string _resetPassword = "";

    [ObservableProperty] private bool _generateResetPassword = true;

    [ObservableProperty] private string? _resetError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDisableOpen))]
    private AdminAccountRow? _disableTarget;

    public bool IsResetOpen => ResetTarget is not null;

    /// <summary>A reset signs that account out everywhere, this PC included when it is your own.</summary>
    public bool ResetIsYou => ResetTarget?.IsYou == true;

    public bool IsDisableOpen => DisableTarget is not null;

    public override async Task ActivateAsync()
    {
        IsBusy = true;
        try
        {
            var page = await Api.GetAdminsAsync(0, AdminApiLimits.DefaultLimit, CancellationToken.None);
            Accounts.Clear();
            Append(page);
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
    private Task RefreshAsync()
    {
        Notice = null;
        return ActivateAsync();
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        IsBusy = true;
        try
        {
            Append(await Api.GetAdminsAsync(_loaded, AdminApiLimits.DefaultLimit, CancellationToken.None));
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
    private void OpenAdd()
    {
        NewUsername = "";
        NewPassword = "";
        GenerateNewPassword = true;
        AddError = null;
        IsAddOpen = true;
    }

    [RelayCommand]
    private void CancelAdd()
    {
        IsAddOpen = false;
        NewPassword = "";
        AddError = null;
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var username = NewUsername.Trim();
        if (username.Length == 0)
        {
            AddError = "Enter a user name.";
            return;
        }

        var password = GenerateNewPassword ? GeneratePassword() : NewPassword;
        // Off the form before the call, so a typed password is not left in a field whatever happens next.
        NewPassword = "";
        if (password.Length < MinimumPasswordLength)
        {
            AddError = $"The password must be at least {MinimumPasswordLength} characters.";
            return;
        }

        IsBusy = true;
        AddError = null;
        try
        {
            var created = await Api.CreateAdminAsync(new AdminAccountCreate(username, password), CancellationToken.None);
            IsAddOpen = false;
            Notice = $"Administrator '{created.Username}' added.";
            if (GenerateNewPassword)
            {
                ShowOnce.Show(
                    $"Password for {created.Username}",
                    "Give this to the new administrator. They sign in with it here or on the web admin pages.",
                    password);
            }
        }
        catch (PortalApiException ex)
        {
            AddError = ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await ActivateAsync();
    }

    [RelayCommand]
    private void OpenReset(AdminAccountRow? row)
    {
        if (row is null)
        {
            return;
        }

        ResetPassword = "";
        GenerateResetPassword = true;
        ResetError = null;
        ResetTarget = row;
    }

    [RelayCommand]
    private void CancelReset()
    {
        ResetTarget = null;
        ResetPassword = "";
        ResetError = null;
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (ResetTarget is not { } target)
        {
            return;
        }

        var password = GenerateResetPassword ? GeneratePassword() : ResetPassword;
        ResetPassword = "";
        if (password.Length < MinimumPasswordLength)
        {
            ResetError = $"The password must be at least {MinimumPasswordLength} characters.";
            return;
        }

        IsBusy = true;
        ResetError = null;
        try
        {
            await Api.ResetAdminPasswordAsync(target.Account.Id, password, CancellationToken.None);
            ResetTarget = null;
            // No reload: a reset changes nothing the table shows, and after resetting your own password
            // the next call is refused, which would sign you out before you had read the new one.
            Notice = target.IsYou
                ? "Your password is changed and your sessions are ended. You will be asked to sign in again with the new password."
                : $"Password changed for '{target.Username}'. That account is signed out everywhere.";
            if (GenerateResetPassword)
            {
                ShowOnce.Show(
                    $"New password for {target.Username}",
                    target.IsYou
                        ? "Sign in again with this. Every session this account held has ended, this one included."
                        : "Give this to the administrator. Every session the account held has ended.",
                    password);
            }
        }
        catch (PortalApiException ex)
        {
            ResetError = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void RequestDisable(AdminAccountRow? row)
    {
        if (row is null)
        {
            return;
        }

        if (row.IsYou)
        {
            // The server refuses this too. Saying so here, before asking, saves a pointless confirmation.
            ErrorMessage = "You cannot disable the account you are signed in with.";
            return;
        }

        DisableTarget = row;
    }

    [RelayCommand]
    private void CancelDisable() => DisableTarget = null;

    [RelayCommand]
    private async Task ConfirmDisableAsync()
    {
        if (DisableTarget is not { } target)
        {
            return;
        }

        DisableTarget = null;
        IsBusy = true;
        try
        {
            var disabled = await Api.DisableAdminAsync(target.Account.Id, CancellationToken.None);
            ErrorMessage = null;
            Notice = $"Administrator '{disabled.Username}' is disabled and signed out.";
        }
        catch (PortalApiException ex)
        {
            Notice = null;
            ErrorMessage = ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await ActivateAsync();
    }

    private void Append(AdminPage<AdminAccount> page)
    {
        foreach (var account in page.Items)
        {
            var isYou = currentUsername is not null
                        && string.Equals(account.Username, currentUsername.Trim(), StringComparison.OrdinalIgnoreCase);
            Accounts.Add(new AdminAccountRow(account, isYou));
        }

        _loaded = page.Offset + page.Items.Count;
        HasMore = page.HasMore;
    }

    private static string GeneratePassword()
        => new(RandomNumberGenerator.GetItems<char>(PasswordAlphabet, GeneratedPasswordLength));
}
