using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using AppPortal.Client.Services;
using AppPortal.Shared;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppPortal.Client.ViewModels.Admin;

/// <summary>
/// One entry in the Source selector. The managers carry the scopes they can really carry out and
/// whether they can pin a version, so the form can say so before a save rather than a device after it.
/// </summary>
public sealed record CatalogSourceOption(
    string Value,
    string DisplayName,
    string Purpose,
    IReadOnlyList<string> Scopes,
    bool CanPinVersion,
    bool IsManager = false);

/// <summary>A value the form stores and the words an administrator reads for it.</summary>
public sealed record CatalogChoice(string Value, string Label);

/// <summary>
/// The catalog edit form, mirrored from the web page after M5-05: one Source selector first, only that
/// source's fields, the shared fields written once, and a sentence saying what a save will do. The
/// web page is the reference; where this differs, this is wrong.
/// </summary>
public sealed partial class CatalogEditorViewModel : ViewModelBase
{
    public const string SourceAction1 = "action1";
    public const string SourceDirect = "direct";

    /// <summary>The web form's create route. An app with this id could never be opened in a browser.</summary>
    public const string ReservedId = "new";

    private static readonly IReadOnlyList<string> BothScopes = ["machine", "user"];

    /// <summary>The same list and the same purpose sentences as the web page's Source selector, in its order.</summary>
    public static IReadOnlyList<CatalogSourceOption> Sources { get; } =
    [
        new(SourceAction1, "Action1",
            "A package from your Action1 Software Repository. Action1 always installs for everyone on the PC.",
            ["machine"], true),
        new(WingetSources.Winget, "winget",
            "Windows applications, from Microsoft's community repository. The usual choice for most software.",
            BothScopes, true),
        new(WingetSources.Store, "Microsoft Store",
            "Store applications, by their product id. They install into the profile of the person who asks.",
            BothScopes, true),
        .. PackageManagers.All.Select(m => new CatalogSourceOption(m.Name, m.DisplayName, m.Purpose, m.Scopes, m.CanPinVersion, IsManager: true)),
        new(SourceDirect, "Direct download",
            "Any installer at a web address, checked against its SHA-256. For game launchers and vendor installers that are in no repository.",
            BothScopes, true),
    ];

    public static IReadOnlyList<CatalogChoice> AllScopes { get; } =
    [
        new("machine", "Everyone on the PC"),
        new("user", "The person who asks for it"),
    ];

    public static IReadOnlyList<CatalogChoice> EngineOverrides { get; } =
    [
        new("", "Follow the server's default"),
        new(EngineLabel.Action1, "Use Action1"),
        new(EngineLabel.Agent, "Use the agent"),
    ];

    public static IReadOnlyList<string> InstallerTypes { get; } = ["exe", "msi", "msix"];

    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Property changes that are not the form's content. Anything else is a field, and changing a
    /// field redoes the sentence and the unsaved-changes check.
    /// </summary>
    private static readonly HashSet<string> NotFields =
    [
        nameof(Sentence), nameof(IsDirty), nameof(IsBusy), nameof(ErrorMessage), nameof(HelperMessage),
        nameof(HelperFailed), nameof(IsConfirmingDiscard), nameof(VerifyMessage), nameof(VerifyOk),
        nameof(PackageSearchMessage), nameof(SelectedSource), nameof(SelectedScope), nameof(SelectedEngine),
        nameof(ScopeOptions), nameof(SourcePurpose), nameof(ShowPackageFields), nameof(ShowDirectFields),
        nameof(ShowAgentFields), nameof(ShowLookup), nameof(ShowStoreHint), nameof(ShowManagerHint),
        nameof(IsAction1Source), nameof(ShowEngineOverride), nameof(SourceIdLabel), nameof(SourceIdPlaceholder),
        nameof(VersionEnabled), nameof(VersionPlaceholder), nameof(Action1Heading), nameof(Title),
    ];

    private readonly IAdminApiClient _api;
    private readonly Action<AdminCatalogApp> _saved;
    private readonly Action _closed;
    private readonly string _baseline;
    /// <summary>True only while the constructor fills the form from a saved app.</summary>
    private readonly bool _loading;

    /// <param name="app">The app to edit, or null for the create form.</param>
    /// <param name="saved">Called with what the server stored, after a save it accepted.</param>
    /// <param name="closed">Called when the form is left without saving.</param>
    public CatalogEditorViewModel(IAdminApiClient api, AdminCatalogApp? app, Action<AdminCatalogApp> saved, Action closed)
    {
        _api = api;
        _saved = saved;
        _closed = closed;
        IsNew = app is null;
        _loading = true;
        if (app is not null)
        {
            Fill(app);
        }

        _loading = false;
        _baseline = Snapshot();
    }

    public bool IsNew { get; }

    public string Title => IsNew ? "New app" : string.IsNullOrWhiteSpace(Name) ? Id : Name;

    [ObservableProperty] private string _id = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string _name = "";

    [ObservableProperty] private string _publisher = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _category = "Other";
    [ObservableProperty] private string _iconUrl = "";
    [ObservableProperty] private bool _featured;
    [ObservableProperty] private bool _hidden;

    /// <summary>Empty to follow the server's default, which is what almost every app should do.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedEngine))]
    private string _engineOverride = "";

    [ObservableProperty] private string _requirements = "";

    /// <summary>App ids this one needs first, one per line, in the order they should be installed.</summary>
    [ObservableProperty] private string _requires = "";

    [ObservableProperty] private bool _userRemovable;
    [ObservableProperty] private string _matchNameContains = "";
    [ObservableProperty] private string _matchNameEquals = "";

    [ObservableProperty]

    [NotifyPropertyChangedFor(nameof(ShowEngineOverride))]

    private string _packageId = "";

    [ObservableProperty] private string _packageVersion = "latest";

    /// <summary>
    /// Where the app comes from: <c>action1</c>, <c>winget</c>, <c>msstore</c>, <c>direct</c>, or the
    /// name of a package manager. No manager is called any of the other four, so one value is enough.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSource), nameof(SourcePurpose), nameof(ShowPackageFields), nameof(ShowDirectFields),
        nameof(ShowAgentFields), nameof(ShowLookup), nameof(ShowStoreHint), nameof(ShowManagerHint), nameof(IsAction1Source),
        nameof(ShowEngineOverride), nameof(SourceIdLabel), nameof(SourceIdPlaceholder), nameof(VersionEnabled),
        nameof(VersionPlaceholder), nameof(ScopeOptions), nameof(SelectedScope), nameof(Action1Heading))]
    private string _source = SourceAction1;

    /// <summary>The package's id in its source, for winget, the Store and every manager.</summary>
    [ObservableProperty] private string _sourceId = "";
    [ObservableProperty] private string _sourceVersion = "";
    [ObservableProperty] private string _sourceExtraArgs = "";

    /// <summary>Machine or user, for every agent source, the direct download too.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedScope))]
    private string _sourceScope = "machine";

    [ObservableProperty] private bool _sourceRequiresReboot;

    [ObservableProperty] private string _directUrl = "";
    [ObservableProperty] private string _directSha256 = "";
    [ObservableProperty] private string _directInstallerType = "exe";
    [ObservableProperty] private string _directSilentArgs = "";

    /// <summary>Text rather than a number, so a half-typed value can be shown back with the reason it is wrong.</summary>
    [ObservableProperty] private string _directSizeBytes = "";

    [ObservableProperty] private string _directUninstallKey = "";

    [ObservableProperty] private bool _isBusy;

    /// <summary>Why the last save did not happen, from this form's own checks or from the server.</summary>
    [ObservableProperty] private string? _errorMessage;

    /// <summary>What the last Look up or Fetch and hash found, shown under the source's fields.</summary>
    [ObservableProperty] private string? _helperMessage;

    [ObservableProperty] private bool _helperFailed;
    [ObservableProperty] private string? _verifyMessage;
    [ObservableProperty] private bool _verifyOk;
    [ObservableProperty] private string? _packageSearchMessage;

    /// <summary>Cancel was pressed with changes on the form; the next step is Discard or Keep editing.</summary>
    [ObservableProperty] private bool _isConfirmingDiscard;

    public ObservableCollection<AdminPackageResult> PackageResults { get; } = [];

    public CatalogSourceOption SelectedSource
    {
        get => Sources.FirstOrDefault(s => s.Value == Source) ?? Sources[0];
        set
        {
            // A combo box sets null while its items are replaced. That is not a choice anybody made.
            if (value is not null)
            {
                Source = value.Value;
            }
        }
    }

    /// <summary>
    /// The scopes this source can really carry out. The web page greys the others out; a combo box
    /// here simply leaves them off, which says the same thing.
    /// </summary>
    public IReadOnlyList<CatalogChoice> ScopeOptions
        => [.. AllScopes.Where(s => SelectedSource.Scopes.Contains(s.Value) || !SelectedSource.IsManager)];

    public CatalogChoice? SelectedScope
    {
        get => ScopeOptions.FirstOrDefault(s => s.Value == SourceScope);
        set
        {
            if (value is not null)
            {
                SourceScope = value.Value;
            }
        }
    }

    public CatalogChoice SelectedEngine
    {
        get => EngineOverrides.FirstOrDefault(e => e.Value == EngineOverride) ?? EngineOverrides[0];
        set
        {
            if (value is not null)
            {
                EngineOverride = value.Value;
            }
        }
    }

    public string SourcePurpose => SelectedSource.Purpose;

    private PackageManagerDescriptor? Manager => PackageManagers.Find(Source);

    public bool IsAction1Source => Source == SourceAction1;

    public bool ShowPackageFields => Source is WingetSources.Winget or WingetSources.Store || Manager is not null;

    public bool ShowDirectFields => Source == SourceDirect;

    public bool ShowAgentFields => !IsAction1Source;

    public bool ShowLookup => Source is WingetSources.Winget or WingetSources.Store;

    public bool ShowStoreHint => Source == WingetSources.Store;

    public bool ShowManagerHint => Manager is not null;

    /// <summary>The override only means something for an app that has both kinds of package.</summary>
    public bool ShowEngineOverride => !IsAction1Source && !string.IsNullOrWhiteSpace(PackageId);

    /// <summary>Action1 as the source is the Action1 section itself, not an optional extra to it.</summary>
    public string Action1Heading => IsAction1Source ? "Action1 package" : "Also offer it through Action1 (optional)";

    public string SourceIdLabel => Source switch
    {
        WingetSources.Store => "Store product id",
        WingetSources.Winget => "Winget id",
        _ => "Package id",
    };

    public string SourceIdPlaceholder => Source switch
    {
        WingetSources.Store => "9WZDNCRFJ3TJ",
        WingetSources.Winget => "Valve.Steam",
        _ when Manager is not null => "7zip",
        _ => "",
    };

    /// <summary>False for a manager that always installs its current version, so none can be asked for.</summary>
    public bool VersionEnabled => Manager is not { CanPinVersion: false };

    public string VersionPlaceholder => VersionEnabled ? "" : "This manager always installs its current version";

    /// <summary>What saving the form will do, in words, worked out from the same app a save sends.</summary>
    public string Sentence => Describe(Build());

    /// <summary>True once the form would save something other than what it opened with.</summary>
    public bool IsDirty => Snapshot() != _baseline;

    partial void OnSourceChanged(string value)
    {
        // Only when the person changes the source, never on load: on load the value is the one that
        // was saved, and moving it would rewrite an administrator's own choice behind their back.
        if (!_loading && value == WingetSources.Store)
        {
            SourceScope = WingetSources.StoreScope;
        }

        // A manager that can only install one way gets that way, as the web page does. A manager
        // app saved with a scope it cannot carry out was refused by the server, so none loads here.
        var allowed = SelectedSource.IsManager ? SelectedSource.Scopes : BothScopes;
        if (!allowed.Contains(SourceScope))
        {
            SourceScope = allowed[0];
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is { } name && !NotFields.Contains(name))
        {
            OnPropertyChanged(nameof(Sentence));
            OnPropertyChanged(nameof(IsDirty));
            IsConfirmingDiscard = false;
        }
    }

    /// <summary>
    /// The app this form describes, for a save and for the sentence alike, tidied the way the web form
    /// tidies it: a blank is nothing, a missing category is Other and a missing version is latest.
    /// </summary>
    public AdminCatalogApp Build()
    {
        var match = string.IsNullOrWhiteSpace(MatchNameContains) && string.IsNullOrWhiteSpace(MatchNameEquals)
            ? null
            : new AdminMatchRule(
                string.IsNullOrWhiteSpace(MatchNameContains) ? null : MatchNameContains.Trim(),
                string.IsNullOrWhiteSpace(MatchNameEquals) ? null : MatchNameEquals.Trim());

        var id = (SourceId ?? "").Trim();
        PackageDefinition? agent = Source switch
        {
            null or "" or SourceAction1 => null,
            WingetSources.Winget or WingetSources.Store => new WingetPackageDefinition(id, SourceScope,
                EmptyToNull(SourceVersion), EmptyToNull(SourceExtraArgs), SourceRequiresReboot, Source),
            SourceDirect => new DirectPackageDefinition((DirectUrl ?? "").Trim(), (DirectSha256 ?? "").Trim(),
                DirectInstallerType, DirectSilentArgs ?? "", SizeBytes() ?? 0, EmptyToNull(DirectUninstallKey),
                SourceScope, SourceRequiresReboot),
            // A version the manager cannot pin is not sent, as a disabled field on the web page is not posted.
            _ when Manager is not null => new ManagedPackageDefinition(Source, id, SourceScope,
                VersionEnabled ? EmptyToNull(SourceVersion) : null, EmptyToNull(SourceExtraArgs), SourceRequiresReboot),
            _ => null,
        };

        return new AdminCatalogApp(
            (Id ?? "").Trim(),
            Name ?? "",
            Publisher ?? "",
            Description ?? "",
            string.IsNullOrWhiteSpace(Category) ? "Other" : Category,
            string.IsNullOrWhiteSpace(IconUrl) ? null : IconUrl.Trim(),
            Featured,
            Hidden,
            EmptyToNull(EngineOverride),
            EmptyToNull(Requirements),
            [.. (Requires ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            UserRemovable,
            match,
            new AdminAction1Package(
                (PackageId ?? "").Trim(),
                string.IsNullOrWhiteSpace(PackageVersion) ? "latest" : PackageVersion.Trim()),
            agent);
    }

    /// <summary>
    /// The server's own rules, run before the request so a mistake is named without a round trip. The
    /// server runs them again on the save and has the last word; its message is shown the same way.
    /// Cycles in the prerequisites are left to it, because only it can see the whole catalog.
    /// </summary>
    public string? Validate(AdminCatalogApp app)
    {
        if (IsNew && string.Equals(app.Id, ReservedId, StringComparison.OrdinalIgnoreCase))
        {
            return $"'{ReservedId}' is reserved for the create form. Give the app another id.";
        }

        if (Source == SourceDirect && SizeBytes() is null)
        {
            return "Enter sizeBytes as a positive whole number of bytes.";
        }

        if (string.IsNullOrWhiteSpace(app.Id))
        {
            return "An app needs an id.";
        }

        if (string.IsNullOrWhiteSpace(app.Name))
        {
            return "An app needs a name.";
        }

        if ((app.Requirements?.Length ?? 0) > CatalogLimits.MaxRequirementsLength)
        {
            return $"Requirements must be {CatalogLimits.MaxRequirementsLength} characters or fewer.";
        }

        if (!HasAction1(app) && app.Agent is null)
        {
            return "An app needs an Action1 package id or an agent package.";
        }

        try
        {
            app.Agent?.Validate();
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }

        return null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ErrorMessage = null;
        var app = Build();
        if (Validate(app) is { } problem)
        {
            ErrorMessage = problem;
            return;
        }

        IsBusy = true;
        try
        {
            // The API writes an app whole under its id, so a new app with a taken id would replace the
            // one already there. The web form refuses that, and so does this.
            if (IsNew && await ExistsAsync(app.Id))
            {
                ErrorMessage = $"An app with id '{app.Id}' already exists.";
                return;
            }

            var stored = await _api.SaveCatalogAppAsync(app, CancellationToken.None);
            _saved(stored);
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
    private void Cancel()
    {
        if (IsDirty)
        {
            IsConfirmingDiscard = true;
            return;
        }

        _closed();
    }

    [RelayCommand]
    private void Discard() => _closed();

    [RelayCommand]
    private void KeepEditing() => IsConfirmingDiscard = false;

    [RelayCommand]
    private async Task LookUpAsync()
    {
        await Helper(async () =>
        {
            var found = await _api.LookupWingetAsync(new AdminPackageRef((SourceId ?? "").Trim(), null,
                Source == WingetSources.Store ? WingetSources.Store : WingetSources.Winget), CancellationToken.None);
            HelperFailed = found.Exists == false;
            HelperMessage = found.Message;
        });
    }

    /// <summary>The server downloads the installer, not this PC, so the hash is of what devices will fetch.</summary>
    [RelayCommand]
    private async Task FetchAndHashAsync()
    {
        await Helper(async () =>
        {
            var result = await _api.HashInstallerAsync((DirectUrl ?? "").Trim(), CancellationToken.None);
            DirectSha256 = result.Sha256;
            DirectSizeBytes = result.SizeBytes.ToString(CultureInfo.InvariantCulture);
            HelperFailed = false;
            HelperMessage = "Hash and size filled. Save to keep the definition.";
        });
    }

    [RelayCommand]
    private async Task SearchPackagesAsync()
    {
        var term = string.IsNullOrWhiteSpace(PackageId) ? Name : PackageId;
        PackageResults.Clear();
        PackageSearchMessage = null;
        IsBusy = true;
        try
        {
            foreach (var package in await _api.SearchAction1PackagesAsync((term ?? "").Trim(), CancellationToken.None))
            {
                PackageResults.Add(package);
            }

            PackageSearchMessage = PackageResults.Count == 0 ? "Nothing in the Software Repository matched." : null;
        }
        catch (PortalApiException ex)
        {
            PackageSearchMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Fills the field rather than saving: the administrator still reviews and saves.</summary>
    [RelayCommand]
    private void UsePackage(AdminPackageResult? package)
    {
        if (package is not null)
        {
            PackageId = package.Id;
        }
    }

    [RelayCommand]
    private async Task VerifyPackageAsync()
    {
        if (string.IsNullOrWhiteSpace(PackageId))
        {
            VerifyOk = false;
            VerifyMessage = "Enter a package id first.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await _api.VerifyAction1PackageAsync(new AdminPackageRef(PackageId.Trim(),
                string.IsNullOrWhiteSpace(PackageVersion) ? "latest" : PackageVersion.Trim()), CancellationToken.None);
            VerifyOk = result.Ok;
            VerifyMessage = result.Message;
        }
        catch (PortalApiException ex)
        {
            VerifyOk = false;
            VerifyMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Where the agent gets <paramref name="agent"/> from, named the way the web catalog names it, or
    /// empty for none. The Source selector, the sentence and the catalog list all say it this way.
    /// </summary>
    public static string SourceName(PackageDefinition? agent) => agent switch
    {
        WingetPackageDefinition { Source: WingetSources.Store } => "Microsoft Store",
        WingetPackageDefinition => "winget",
        DirectPackageDefinition => "Direct download",
        ManagedPackageDefinition managed => PackageManagers.Find(managed.Manager)?.DisplayName ?? managed.Manager,
        _ => "",
    };

    public static bool HasAction1(AdminCatalogApp app) => !string.IsNullOrWhiteSpace(app.Action1?.PackageId);

    /// <summary>
    /// What saving <paramref name="app"/> will do, in one sentence: the app, who it installs for, and
    /// where it comes from. The web page's sentence is <c>CatalogEntry.Describe</c>, which lives in the
    /// server and works on the server's own entry type, so it is written again here over the wire
    /// shape, word for word, and held to the same example sentences by the tests.
    /// </summary>
    public static string Describe(AdminCatalogApp app)
    {
        var name = string.IsNullOrWhiteSpace(app.Name) ? "this app" : app.Name.Trim();
        if (app.Agent is null)
        {
            return HasAction1(app)
                ? $"Installs {name} for everyone on the PC, through Action1."
                : $"{name} has no source yet, so no device can install it.";
        }

        var agent = $"{For(app.Agent.Scope)}, {Through(app.Agent)}";
        if (!HasAction1(app))
        {
            return $"Installs {name} {agent}.";
        }

        var either = app.EngineOverride switch
        {
            EngineLabel.Action1 => " A device that could use either uses Action1.",
            EngineLabel.Agent => " A device that could use either uses the agent.",
            _ => "",
        };
        return $"Installs {name} for everyone on the PC through Action1, or {agent} on a device with only the agent.{either}";

        static string For(string scope) => scope == "user" ? "for the person who asks for it" : "for everyone on the PC";

        static string Through(PackageDefinition agent) => agent switch
        {
            WingetPackageDefinition { Source: WingetSources.Store } => "from the Microsoft Store",
            DirectPackageDefinition direct => "with its own installer from "
                                              + (Uri.TryCreate(direct.Url, UriKind.Absolute, out var url) ? url.Host : "its download address"),
            _ => "through " + SourceName(agent),
        };
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>The download size, or null when the box holds anything but a positive whole number.</summary>
    private long? SizeBytes()
        => long.TryParse((DirectSizeBytes ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var size) && size > 0
            ? size
            : null;

    /// <summary>What the form would save, as text, so two states compare by content and not by reference.</summary>
    private string Snapshot() => JsonSerializer.Serialize(Build(), SnapshotJson);

    private async Task<bool> ExistsAsync(string id)
    {
        try
        {
            await _api.GetCatalogAppAsync(id, CancellationToken.None);
            return true;
        }
        catch (PortalApiException ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task Helper(Func<Task> run)
    {
        HelperMessage = null;
        HelperFailed = false;
        IsBusy = true;
        try
        {
            await run();
        }
        catch (PortalApiException ex)
        {
            HelperFailed = true;
            HelperMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Fill(AdminCatalogApp app)
    {
        Id = app.Id;
        Name = app.Name;
        Publisher = app.Publisher;
        Description = app.Description;
        Category = app.Category;
        IconUrl = app.IconUrl ?? "";
        Featured = app.Featured;
        Hidden = app.Hidden;
        EngineOverride = app.EngineOverride ?? "";
        Requirements = app.Requirements ?? "";
        Requires = string.Join('\n', app.Requires ?? []);
        UserRemovable = app.UserRemovable;
        MatchNameContains = app.Match?.NameContains ?? "";
        MatchNameEquals = app.Match?.NameEquals ?? "";
        PackageId = app.Action1?.PackageId ?? "";
        PackageVersion = app.Action1?.Version ?? "latest";
        if (app.Agent is { } agent)
        {
            SourceScope = agent.Scope;
            SourceRequiresReboot = agent.RequiresReboot;
        }

        switch (app.Agent)
        {
            case WingetPackageDefinition winget:
                SourceId = winget.Id;
                SourceVersion = winget.Version ?? "";
                SourceExtraArgs = winget.ExtraArgs ?? "";
                Source = winget.Source;
                break;
            case DirectPackageDefinition direct:
                DirectUrl = direct.Url;
                DirectSha256 = direct.Sha256;
                DirectInstallerType = direct.InstallerType;
                DirectSilentArgs = direct.SilentArgs;
                DirectSizeBytes = direct.SizeBytes.ToString(CultureInfo.InvariantCulture);
                DirectUninstallKey = direct.UninstallKey ?? "";
                Source = SourceDirect;
                break;
            case ManagedPackageDefinition managed:
                SourceId = managed.Id;
                SourceVersion = managed.Version ?? "";
                SourceExtraArgs = managed.ExtraArgs ?? "";
                Source = managed.Manager;
                break;
            default:
                Source = SourceAction1;
                break;
        }
    }
}
