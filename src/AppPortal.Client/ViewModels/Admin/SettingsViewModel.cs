using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Client.Services;
using AppPortal.Shared;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>The settings the whole server shares, as the web settings page edits them: the default engine.</summary>
public sealed partial class SettingsViewModel(IAdminApiClient api) : AdminPageViewModel(api)
{
    /// <summary>
    /// One choice in an engine list: the value the server stores and the word a person reads. The
    /// device and key pages use it too. Nested, so a type of the same name on another admin page cannot
    /// clash with it.
    /// </summary>
    public sealed record EngineChoice(string Value, string Label)
    {
        /// <summary>What a combo box shows, so a list of these needs no item template.</summary>
        public override string ToString() => Label;
    }

    /// <summary>The two the server accepts. Anything else it refuses, so nothing else is offered.</summary>
    public IReadOnlyList<EngineChoice> Engines { get; } =
    [
        new(EngineLabel.Action1, "Action1"),
        new(EngineLabel.Agent, "Agent"),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private EngineChoice? _defaultEngine;

    /// <summary>What the server last said it holds, so Save lights up only for a change.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string? _savedEngine;

    [ObservableProperty] private string? _notice;

    public override async Task ActivateAsync()
    {
        IsBusy = true;
        try
        {
            Show(await Api.GetSettingsAsync(CancellationToken.None));
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

    private bool CanSave() => DefaultEngine is not null && DefaultEngine.Value != SavedEngine && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (DefaultEngine is null)
        {
            return;
        }

        IsBusy = true;
        Notice = null;
        try
        {
            Show(await Api.UpdateSettingsAsync(new AdminSettings(DefaultEngine.Value), CancellationToken.None));
            ErrorMessage = null;
            Notice = $"Saved. Devices that could use either engine now install through {DefaultEngine?.Label}.";
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    private void Show(AdminSettings settings)
    {
        SavedEngine = settings.DefaultEngine;
        DefaultEngine = Engines.FirstOrDefault(e => e.Value == settings.DefaultEngine);
    }
}
