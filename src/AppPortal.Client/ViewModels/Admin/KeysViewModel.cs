using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Client.Services;
using AppPortal.Shared;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>One enrollment key as the table shows it. The secret is never part of this.</summary>
public sealed record EnrollmentKeyRow(EnrollmentKeySummary Key)
{
    /// <summary>The server stores the key's first characters without the prefix every key shares.</summary>
    private const string KeyPrefix = "ape_";

    public string Name => Key.Name;

    public string PrefixText => (Key.KeyPrefix.StartsWith(KeyPrefix, StringComparison.Ordinal) ? Key.KeyPrefix : KeyPrefix + Key.KeyPrefix) + "…";

    public string EngineText => Key.DefaultEngine;

    /// <summary>"3 of 10", or "3" when the key has no limit, as the web table says it.</summary>
    public string UsesText => Key.MaxUses is { } max
        ? $"{Key.Uses.ToString(CultureInfo.CurrentCulture)} of {max.ToString(CultureInfo.CurrentCulture)}"
        : Key.Uses.ToString(CultureInfo.CurrentCulture);

    public string ExpiresText => Key.ExpiresAt is { } at ? at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "never";

    public bool IsActive => string.Equals(Key.Status, "active", StringComparison.OrdinalIgnoreCase);

    public string StatusText => Key.Status.Length == 0 ? "" : char.ToUpperInvariant(Key.Status[0]) + Key.Status[1..];

    public string CreatedText => $"{Key.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} by {Key.CreatedBy}";

    /// <summary>Revoking twice is harmless on the server, but a button that does nothing is not offered.</summary>
    public bool CanRevoke => Key.RevokedAt is null;
}

/// <summary>One enrollment attempt with a key, from the key's audit trail.</summary>
public sealed record EnrollmentKeyEventRow(EnrollmentKeyEvent Event)
{
    public string WhenText => Event.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary>A dash for an attempt that made no device; "removed" for a device that has gone since.</summary>
    public string DeviceText => Event.DeviceName ?? (Event.DeviceId is null ? "—" : "removed");

    public string Source => Event.Source;

    public string Description => Event.Description;

    public bool Succeeded => Event.Outcome is "enrolled" or "re-enrolled";
}

/// <summary>
/// Enrollment keys, as the web keys pages work them: the list, a new key shown once, revoking, and
/// one key's enrollment attempts.
/// </summary>
public sealed partial class KeysViewModel(IAdminApiClient api) : AdminPageViewModel(api)
{
    private int _loaded;

    public ObservableCollection<EnrollmentKeyRow> Keys { get; } = [];

    public ObservableCollection<EnrollmentKeyEventRow> Events { get; } = [];

    public ShowOnceViewModel ShowOnce { get; } = new();

    /// <summary>The three a key can enroll a PC for, as the web form offers them.</summary>
    public IReadOnlyList<SettingsViewModel.EngineChoice> Engines { get; } =
    [
        new(EngineLabel.Action1, "Action1"),
        new(EngineLabel.Agent, "Agent"),
        new("both", "Both"),
    ];

    [ObservableProperty] private bool _hasMore;

    [ObservableProperty] private string? _notice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDetailOpen), nameof(IsListOpen))]
    private EnrollmentKeyRow? _selectedKey;

    [ObservableProperty] private bool _isCreateOpen;

    [ObservableProperty] private string _newName = "";

    [ObservableProperty] private SettingsViewModel.EngineChoice? _newEngine;

    [ObservableProperty] private string _newExpires = "";

    [ObservableProperty] private string _newMaxUses = "";

    [ObservableProperty] private string? _createError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRevokeOpen))]
    private EnrollmentKeyRow? _revokeTarget;

    public bool IsDetailOpen => SelectedKey is not null;

    public bool IsListOpen => SelectedKey is null;

    public bool IsRevokeOpen => RevokeTarget is not null;

    public override async Task ActivateAsync()
    {
        IsBusy = true;
        try
        {
            var page = await Api.GetKeysAsync(0, AdminApiLimits.DefaultLimit, CancellationToken.None);
            Keys.Clear();
            Append(page);
            if (SelectedKey is { } open)
            {
                await LoadKeyAsync(open.Key.Id);
            }

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
            Append(await Api.GetKeysAsync(_loaded, AdminApiLimits.DefaultLimit, CancellationToken.None));
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
    private async Task OpenKeyAsync(EnrollmentKeyRow? row)
    {
        if (row is null)
        {
            return;
        }

        Notice = null;
        Events.Clear();
        SelectedKey = row;
        IsBusy = true;
        try
        {
            await LoadKeyAsync(row.Key.Id);
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
    private void Back()
    {
        SelectedKey = null;
        Events.Clear();
        ErrorMessage = null;
    }

    [RelayCommand]
    private void OpenCreate()
    {
        NewName = "";
        NewEngine = Engines[0];
        NewExpires = "";
        NewMaxUses = "";
        CreateError = null;
        IsCreateOpen = true;
    }

    [RelayCommand]
    private void CancelCreate()
    {
        IsCreateOpen = false;
        CreateError = null;
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        DateTimeOffset? expires;
        int? maxUses;
        try
        {
            expires = ParseExpiry(NewExpires);
            maxUses = ParseMaxUses(NewMaxUses);
        }
        catch (FormatException ex)
        {
            CreateError = ex.Message;
            return;
        }

        IsBusy = true;
        CreateError = null;
        try
        {
            var created = await Api.CreateKeyAsync(
                new EnrollmentKeyCreate(NewName.Trim(), NewEngine?.Value ?? EngineLabel.Action1, expires, maxUses),
                CancellationToken.None);
            IsCreateOpen = false;
            Notice = $"Key '{created.Key.Name}' created.";
            ShowOnce.Show(
                $"Copy the key for {created.Key.Name}",
                "Only its hash is stored, so this is the one time it can be read. Enter it in the App Portal setup wizard on each PC. If it is lost, revoke the key and create another.",
                created.Plaintext);
        }
        catch (PortalApiException ex)
        {
            CreateError = ex.Message;
            return;
        }
        finally
        {
            IsBusy = false;
        }

        await ActivateAsync();
    }

    [RelayCommand]
    private void RequestRevoke(EnrollmentKeyRow? row) => RevokeTarget = row is { CanRevoke: true } ? row : null;

    [RelayCommand]
    private void CancelRevoke() => RevokeTarget = null;

    [RelayCommand]
    private async Task ConfirmRevokeAsync()
    {
        if (RevokeTarget is not { } target)
        {
            return;
        }

        RevokeTarget = null;
        IsBusy = true;
        try
        {
            await Api.RevokeKeyAsync(target.Key.Id, CancellationToken.None);
            ErrorMessage = null;
            Notice = $"'{target.Name}' is revoked. Machines that have not enrolled with it yet no longer can.";
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

    /// <summary>
    /// The web form's rule: a bare date means the end of that day in UTC, not midnight at its start, so
    /// someone typing today's date gets a key good for the rest of today rather than one already expired.
    /// </summary>
    public static DateTimeOffset? ParseExpiry(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (DateTime.TryParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return new DateTimeOffset(date.Date.AddDays(1).AddTicks(-1), TimeSpan.Zero);
        }

        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }

        throw new FormatException($"'{trimmed}' is not a date. Write it as YYYY-MM-DD.");
    }

    public static int? ParseMaxUses(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var max)
            ? max
            : throw new FormatException($"'{trimmed}' is not a whole number of uses.");
    }

    private async Task LoadKeyAsync(string id)
    {
        var key = await Api.GetKeyAsync(id, CancellationToken.None);
        var events = await Api.GetKeyEventsAsync(id, null, CancellationToken.None);
        SelectedKey = new EnrollmentKeyRow(key);
        Events.Clear();
        foreach (var attempt in events)
        {
            Events.Add(new EnrollmentKeyEventRow(attempt));
        }
    }

    private void Append(AdminPage<EnrollmentKeySummary> page)
    {
        foreach (var key in page.Items)
        {
            Keys.Add(new EnrollmentKeyRow(key));
        }

        _loaded = page.Offset + page.Items.Count;
        HasMore = page.HasMore;
    }
}
