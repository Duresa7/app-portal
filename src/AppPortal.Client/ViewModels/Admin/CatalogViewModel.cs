using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Client.Services;
using AppPortal.Shared;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>
/// The file dialogs Import and Export need. The view supplies them, because a file picker belongs to a
/// window; a test supplies text instead.
/// </summary>
public interface ICatalogFiles
{
    /// <summary>The chosen catalog file's text, or null when the person closed the picker.</summary>
    Task<string?> OpenAsync();

    /// <summary>Writes <paramref name="json"/> where the person chose. The file's name, or null when they closed the picker.</summary>
    Task<string?> SaveAsync(string json);
}

/// <summary>
/// The catalog: every app, a search, New, Import and Export, and Hide and Delete on each row. The
/// editor opens over the list on the same page rather than in a dialog, because it is too big for one.
/// </summary>
public sealed partial class CatalogViewModel(IAdminApiClient api) : AdminPageViewModel(api)
{
    private readonly List<AdminCatalogApp> _all = [];

    public ObservableCollection<CatalogRowViewModel> Apps { get; } = [];

    public static IReadOnlyList<string> Visibilities { get; } = ["All apps", "Visible", "Hidden"];

    [ObservableProperty] private string _searchText = "";

    /// <summary>An index into <see cref="Visibilities"/>.</summary>
    [ObservableProperty] private int _visibility;

    /// <summary>The form over the list, or null while the list is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    private CatalogEditorViewModel? _editor;

    /// <summary>What the last action did, in the web page's words.</summary>
    [ObservableProperty] private string? _notice;

    /// <summary>The app Delete was pressed on, waiting for the person to confirm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeletePrompt))]
    private CatalogRowViewModel? _confirmingDelete;

    /// <summary>
    /// The app the server would not delete because installs refer to it. The message says to hide it
    /// instead, so the button to do that sits beside the message.
    /// </summary>
    [ObservableProperty] private CatalogRowViewModel? _refusedDelete;

    public bool IsEditing => Editor is not null;

    public string DeletePrompt => ConfirmingDelete is { } row
        ? $"Delete {row.Name} from the catalog? Hiding keeps its install history."
        : "";

    public ICatalogFiles? Files { get; set; }

    /// <summary>How many apps the list holds, for the line above it.</summary>
    public string CountText => _all.Count == Apps.Count
        ? $"{_all.Count} app{(_all.Count == 1 ? "" : "s")}"
        : $"{Apps.Count} of {_all.Count} apps";

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnVisibilityChanged(int value) => ApplyFilter();

    public override Task ActivateAsync()
    {
        // Leaving the page with the form open and coming back finds the form as it was. Reloading the
        // list under it would be harmless, but closing it would throw away what somebody typed.
        return IsEditing ? Task.CompletedTask : LoadAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void New()
    {
        Notice = null;
        ErrorMessage = null;
        Editor = new CatalogEditorViewModel(Api, null, OnSaved, CloseEditor);
    }

    /// <summary>Opens the app as the server has it now, not as the list last read it.</summary>
    public async Task EditAsync(CatalogRowViewModel row)
    {
        Notice = null;
        ErrorMessage = null;
        await Run(async () =>
        {
            var app = await Api.GetCatalogAppAsync(row.App.Id, CancellationToken.None);
            Editor = new CatalogEditorViewModel(Api, app, OnSaved, CloseEditor);
        });
    }

    public async Task SetHiddenAsync(CatalogRowViewModel row, bool hidden)
    {
        await Run(async () =>
        {
            var app = await Api.SetCatalogAppHiddenAsync(row.App.Id, hidden, CancellationToken.None);
            Replace(app);
            RefusedDelete = null;
            Notice = hidden
                ? $"'{app.Id}' is hidden. Devices are no longer offered it."
                : $"'{app.Id}' is visible to devices again.";
        });
    }

    public void AskToDelete(CatalogRowViewModel row)
    {
        Notice = null;
        ErrorMessage = null;
        RefusedDelete = null;
        ConfirmingDelete = row;
    }

    [RelayCommand]
    private void KeepApp() => ConfirmingDelete = null;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (ConfirmingDelete is not { } row)
        {
            return;
        }

        ConfirmingDelete = null;
        try
        {
            IsBusy = true;
            await Api.DeleteCatalogAppAsync(row.App.Id, CancellationToken.None);
            _all.RemoveAll(a => a.Id == row.App.Id);
            ApplyFilter();
            Notice = $"'{row.App.Id}' is deleted.";
        }
        catch (PortalApiException ex)
        {
            ErrorMessage = ex.Message;
            // Refused because history refers to it: the one thing left to do is what the message says.
            RefusedDelete = ex.Status == HttpStatusCode.Conflict ? row : null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task HideInsteadAsync()
        => RefusedDelete is { } row ? SetHiddenAsync(row, true) : Task.CompletedTask;

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (Files is null)
        {
            return;
        }

        string? json;
        try
        {
            json = await Files.OpenAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = "Could not read that file. " + ex.Message;
            return;
        }

        if (json is not null)
        {
            await ImportTextAsync(json);
        }
    }

    /// <summary>
    /// Sends a catalog file as it is. Apps are matched by id and every field comes from the file;
    /// apps the file does not mention are left alone, and nothing is deleted.
    /// </summary>
    public async Task ImportTextAsync(string json)
    {
        Notice = null;
        ErrorMessage = null;
        RefusedDelete = null;
        if (json.Trim().Length == 0)
        {
            ErrorMessage = "Choose a catalog file to import.";
            return;
        }

        // Bytes as UTF-8, which is what the server counts. A count of characters would send a file of
        // multi-byte characters the server is bound to refuse.
        if (Encoding.UTF8.GetByteCount(json) > AdminApiLimits.MaxImportBytes)
        {
            ErrorMessage = $"A catalog file may be at most {AdminApiLimits.MaxImportBytes / (1024 * 1024)} MB.";
            return;
        }

        await Run(async () =>
        {
            var result = await Api.ImportCatalogAsync(json, CancellationToken.None);
            await LoadCoreAsync();
            Notice = $"Imported {result.Imported} app{(result.Imported == 1 ? "" : "s")}.";
        });
    }

    /// <summary>The server's own export, byte for byte, so the file imports on the web page unchanged.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Files is null)
        {
            return;
        }

        Notice = null;
        ErrorMessage = null;
        await Run(async () =>
        {
            var json = await Api.ExportCatalogAsync(CancellationToken.None);
            try
            {
                if (await Files.SaveAsync(json) is { } name)
                {
                    Notice = $"Exported the catalog to {name}.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = "Could not write that file. " + ex.Message;
            }
        });
    }

    private async Task LoadAsync()
    {
        await Run(LoadCoreAsync);
    }

    /// <summary>
    /// Every page of the catalog, not the first. The search and the hidden filter work on what is
    /// loaded, so an app on the second page would otherwise be one nobody could find.
    /// </summary>
    private async Task LoadCoreAsync()
    {
        var all = new List<AdminCatalogApp>();
        var offset = 0;
        while (true)
        {
            var page = await Api.GetCatalogAsync(null, offset, AdminApiLimits.MaxLimit, CancellationToken.None);
            all.AddRange(page.Items);
            if (!page.HasMore || page.Items.Count == 0)
            {
                break;
            }

            offset += page.Items.Count;
        }

        _all.Clear();
        _all.AddRange(all);
        ErrorMessage = null;
        ApplyFilter();
    }

    private async Task Run(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
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

    private void OnSaved(AdminCatalogApp app)
    {
        Replace(app);
        Editor = null;
        Notice = $"Saved {app.Name}. Devices pick this up on their next refresh.";
    }

    private void CloseEditor() => Editor = null;

    private void Replace(AdminCatalogApp app)
    {
        var index = _all.FindIndex(a => string.Equals(a.Id, app.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _all[index] = app;
        }
        else
        {
            _all.Add(app);
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        Apps.Clear();
        foreach (var app in _all)
        {
            var shown = Visibility switch
            {
                1 => !app.Hidden,
                2 => app.Hidden,
                _ => true,
            };
            var row = new CatalogRowViewModel(this, app);
            if (shown && (term.Length == 0
                          || app.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                          || app.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
                          || app.Publisher.Contains(term, StringComparison.OrdinalIgnoreCase)
                          || app.Category.Contains(term, StringComparison.OrdinalIgnoreCase)
                          || row.Sources.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                Apps.Add(row);
            }
        }

        OnPropertyChanged(nameof(CountText));
    }
}

/// <summary>One app in the catalog list. Its buttons act through the page, which owns the list.</summary>
public sealed partial class CatalogRowViewModel(CatalogViewModel page, AdminCatalogApp app) : ViewModelBase
{
    public AdminCatalogApp App { get; } = app;

    public string Name => App.Name;

    /// <summary>The id and the category on one line under the name, where the web list puts the id.</summary>
    public string Detail => $"{App.Id} · {App.Category}";

    public string Publisher => App.Publisher;

    public bool Featured => App.Featured;

    public bool Hidden => App.Hidden;

    public string StatusText => App.Hidden ? "Hidden" : "Visible";

    public string HideLabel => App.Hidden ? "Show" : "Hide";

    /// <summary>
    /// Every source the app has, named the way the editor names it. The web list used to say Action1
    /// or none, so an app the agent installs read as having nothing at all; this says what it has.
    /// </summary>
    public string Sources
    {
        get
        {
            var named = new[]
            {
                CatalogEditorViewModel.HasAction1(App) ? "Action1" : "",
                CatalogEditorViewModel.SourceName(App.Agent),
            }.Where(s => s.Length > 0).ToList();
            return named.Count == 0 ? "none" : string.Join(", ", named);
        }
    }

    [RelayCommand]
    private Task EditAsync() => page.EditAsync(this);

    [RelayCommand]
    private Task ToggleHiddenAsync() => page.SetHiddenAsync(this, !App.Hidden);

    [RelayCommand]
    private void Delete() => page.AskToDelete(this);
}
