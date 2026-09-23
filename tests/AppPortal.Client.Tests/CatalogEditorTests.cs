using System.Net;
using System.Text;
using System.Text.Json;

using AppPortal.Client.Services;
using AppPortal.Client.ViewModels.Admin;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

/// <summary>
/// The client's catalog editor against the web form it mirrors: every source reaches the definition
/// the web form would write, an app of every shape opens and saves back as it was, and the sentence
/// under the form reads the same as the web page's.
/// </summary>
public sealed class CatalogEditorTests
{
    private const string Sha = "3f1c7d0e9b8a6f5e4d3c2b1a0f9e8d7c6b5a4f3e2d1c0b9a8f7e6d5c4b3a2f10";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One app of every shape the catalog can hold, each with the optional fields filled in somewhere.</summary>
    public static IEnumerable<object[]> Shapes()
    {
        static object[] Row(AdminCatalogApp app) => [app];

        yield return Row(new AdminCatalogApp("chrome", "Google Chrome", "Google LLC", "A browser.", "Browsers",
            "https://icons.example/chrome.png", Featured: true, Requires: [], Action1: new AdminAction1Package("Google_Chrome_builtin")));
        yield return Row(new AdminCatalogApp("vscode", "Visual Studio Code", "Microsoft", "", "Developer tools",
            Requires: ["git", "node"], Match: new AdminMatchRule("Visual Studio Code", null), Action1: new AdminAction1Package(""),
            Agent: new WingetPackageDefinition("Microsoft.VisualStudioCode", "user", "1.93.0", "--override \"/SILENT\"", true)));
        yield return Row(new AdminCatalogApp("whatsapp", "WhatsApp", Requires: [], Requirements: "Needs the Microsoft Store signed in.",
            Action1: new AdminAction1Package(""), Agent: new WingetPackageDefinition("9NKSQGP7F2NH", "user", Source: WingetSources.Store)));
        yield return Row(new AdminCatalogApp("steam", "Steam", "Valve", "", "Games", Hidden: true, Requires: [], UserRemovable: true,
            Match: new AdminMatchRule(null, "Steam"), Action1: new AdminAction1Package(""),
            Agent: new DirectPackageDefinition("https://cdn.vendor.example/SteamSetup.exe", Sha, "exe", "/S", 3_145_728, "Steam", "user", true)));
        yield return Row(new AdminCatalogApp("both", "Firefox", Requires: [], EngineOverride: EngineLabel.Action1,
            Action1: new AdminAction1Package("Mozilla_Firefox", "128.0"), Agent: new WingetPackageDefinition("Mozilla.Firefox", "machine")));
        foreach (var manager in PackageManagers.All)
        {
            yield return Row(new AdminCatalogApp("m-" + manager.Name, "zlib from " + manager.DisplayName, Requires: [],
                Action1: new AdminAction1Package(""),
                Agent: new ManagedPackageDefinition(manager.Name, "zlib", manager.DefaultScope,
                    manager.CanPinVersion ? "1.3.1" : null, manager.Name == "choco" ? "--ignore-checksums" : null)));
        }
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void An_existing_app_opens_and_builds_back_unchanged(AdminCatalogApp app)
    {
        var editor = Editor(app);

        Assert.Equal(Text(app), Text(editor.Build()));
        Assert.False(editor.IsDirty);
        Assert.Null(editor.Validate(editor.Build()));
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task An_existing_app_saves_back_unchanged(AdminCatalogApp app)
    {
        var api = await SignedInDemo();
        AdminCatalogApp? stored = null;
        var editor = new CatalogEditorViewModel(api, app, saved => stored = saved, () => { });

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(editor.ErrorMessage);
        Assert.NotNull(stored);
        Assert.Equal(Text(app), Text(stored));
        Assert.Equal(Text(app), Text(await api.GetCatalogAppAsync(app.Id, CancellationToken.None)));
    }

    [Fact]
    public void Every_source_in_the_selector_maps_to_its_definition_and_back()
    {
        foreach (var source in CatalogEditorViewModel.Sources)
        {
            var editor = Editor(null);
            editor.Id = "app";
            editor.Name = "App";
            editor.Source = source.Value;
            editor.SourceId = source.Value == WingetSources.Store ? "9WZDNCRFJ3TJ" : source.Value == WingetSources.Winget ? "Vendor.App" : "zlib";
            editor.PackageId = source.Value == CatalogEditorViewModel.SourceAction1 ? "Vendor_App" : "";
            editor.DirectUrl = "https://vendor.example/setup.msi";
            editor.DirectSha256 = Sha;
            editor.DirectInstallerType = "msi";
            editor.DirectSilentArgs = "ALLUSERS=1";
            editor.DirectSizeBytes = "1024";

            var built = editor.Build();
            Assert.Null(editor.Validate(built));
            switch (source.Value)
            {
                case CatalogEditorViewModel.SourceAction1:
                    Assert.Null(built.Agent);
                    Assert.Equal("Vendor_App", built.Action1?.PackageId);
                    break;
                case WingetSources.Winget or WingetSources.Store:
                    var winget = Assert.IsType<WingetPackageDefinition>(built.Agent);
                    Assert.Equal(source.Value, winget.Source);
                    break;
                case CatalogEditorViewModel.SourceDirect:
                    var direct = Assert.IsType<DirectPackageDefinition>(built.Agent);
                    Assert.Equal(1024, direct.SizeBytes);
                    Assert.Equal("msi", direct.InstallerType);
                    break;
                default:
                    var managed = Assert.IsType<ManagedPackageDefinition>(built.Agent);
                    Assert.Equal(source.Value, managed.Manager);
                    Assert.Contains(managed.Scope, source.Scopes);
                    break;
            }

            // And back: the definition opens on the source it was written from.
            var reopened = Editor(built);
            Assert.Equal(source.Value, reopened.Source);
            Assert.Equal(Text(built), Text(reopened.Build()));
        }
    }

    [Fact]
    public void Only_the_chosen_sources_fields_are_shown()
    {
        var editor = Editor(null);
        Assert.True(editor.IsAction1Source);
        Assert.False(editor.ShowAgentFields);
        Assert.False(editor.ShowPackageFields);
        Assert.Equal("Action1 package", editor.Action1Heading);

        editor.Source = WingetSources.Store;
        Assert.True(editor.ShowPackageFields && editor.ShowLookup && editor.ShowStoreHint && editor.ShowAgentFields);
        Assert.False(editor.ShowDirectFields || editor.ShowManagerHint);
        Assert.Equal("Store product id", editor.SourceIdLabel);
        Assert.Equal("Also offer it through Action1 (optional)", editor.Action1Heading);

        editor.Source = "scoop";
        Assert.True(editor.ShowPackageFields && editor.ShowManagerHint);
        Assert.False(editor.ShowLookup || editor.ShowStoreHint || editor.ShowDirectFields);
        Assert.Equal(PackageManagers.Find("scoop")!.Purpose, editor.SourcePurpose);

        editor.Source = CatalogEditorViewModel.SourceDirect;
        Assert.True(editor.ShowDirectFields && editor.ShowAgentFields);
        Assert.False(editor.ShowPackageFields);
    }

    [Fact]
    public void The_engine_override_is_offered_only_when_both_packages_exist()
    {
        var editor = Editor(null);
        editor.PackageId = "Mozilla_Firefox";
        Assert.False(editor.ShowEngineOverride);

        editor.Source = WingetSources.Winget;
        Assert.True(editor.ShowEngineOverride);

        editor.PackageId = " ";
        Assert.False(editor.ShowEngineOverride);
    }

    [Fact]
    public void A_manager_offers_only_the_scopes_it_can_carry_out()
    {
        var editor = Editor(null);
        editor.Source = WingetSources.Winget;
        Assert.Equal(["machine", "user"], editor.ScopeOptions.Select(s => s.Value));

        // Cargo installs into a profile whatever it is asked, so machine is not on offer and not kept.
        editor.Source = "cargo";
        Assert.Equal(["user"], editor.ScopeOptions.Select(s => s.Value));
        Assert.Equal("user", editor.SourceScope);

        editor.Source = "choco";
        Assert.Equal("machine", editor.SourceScope);
    }

    [Fact]
    public void Choosing_the_store_makes_it_per_person_but_opening_a_store_app_keeps_its_scope()
    {
        var editor = Editor(null);
        editor.Source = WingetSources.Store;
        Assert.Equal("user", editor.SourceScope);

        var saved = new AdminCatalogApp("store", "Store app", Action1: new AdminAction1Package(""),
            Agent: new WingetPackageDefinition("9WZDNCRFJ3TJ", "machine", Source: WingetSources.Store));
        Assert.Equal("machine", Editor(saved).SourceScope);
    }

    [Fact]
    public void A_manager_that_cannot_pin_a_version_does_not_send_one()
    {
        var editor = Editor(null);
        editor.Source = "vcpkg";
        editor.SourceId = "zlib";
        editor.SourceVersion = "1.3.1";

        Assert.False(editor.VersionEnabled);
        Assert.Null(Assert.IsType<ManagedPackageDefinition>(editor.Build().Agent).Version);
    }

    [Fact]
    public void The_form_checks_the_servers_rules_before_it_asks()
    {
        var editor = Editor(null);
        Assert.Equal("An app needs an id.", editor.Validate(editor.Build()));

        editor.Id = "new";
        Assert.Equal("'new' is reserved for the create form. Give the app another id.", editor.Validate(editor.Build()));

        editor.Id = "app";
        Assert.Equal("An app needs a name.", editor.Validate(editor.Build()));

        editor.Name = "App";
        Assert.Equal("An app needs an Action1 package id or an agent package.", editor.Validate(editor.Build()));

        editor.Source = WingetSources.Winget;
        editor.SourceId = "not-an-id";
        Assert.Equal("A winget package needs an id such as Valve.Steam.", editor.Validate(editor.Build()));

        editor.Source = CatalogEditorViewModel.SourceDirect;
        editor.DirectSizeBytes = "big";
        Assert.Equal("Enter sizeBytes as a positive whole number of bytes.", editor.Validate(editor.Build()));
    }

    [Fact]
    public async Task A_refused_save_shows_the_servers_message_and_stays_on_the_form()
    {
        var api = Api(HttpStatusCode.UnprocessableEntity, """{"message":"'a' needs 'b' first, and 'b' needs 'a'. Remove one of them."}""");
        var closed = false;
        var editor = new CatalogEditorViewModel(api, Valid(), _ => closed = true, () => closed = true);

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("'a' needs 'b' first, and 'b' needs 'a'. Remove one of them.", editor.ErrorMessage);
        Assert.False(closed);
        Assert.False(editor.IsBusy);
    }

    [Fact]
    public async Task A_refused_session_on_save_ends_the_session_the_usual_way()
    {
        var api = Api(HttpStatusCode.Unauthorized, "");
        var ended = false;
        api.Unauthorized += (_, _) => ended = true;
        var editor = new CatalogEditorViewModel(api, Valid(), _ => { }, () => { });

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.True(ended);
        Assert.Equal(AdminApiClient.SessionEndedMessage, editor.ErrorMessage);
    }

    [Fact]
    public async Task A_new_app_may_not_take_an_id_that_is_already_in_the_catalog()
    {
        var api = await SignedInDemo();
        var editor = new CatalogEditorViewModel(api, null, _ => Assert.Fail("saved"), () => { });
        editor.Id = "google-chrome";
        editor.Name = "Another Chrome";
        editor.PackageId = "Another";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("An app with id 'google-chrome' already exists.", editor.ErrorMessage);
        Assert.Equal("Google Chrome", (await api.GetCatalogAppAsync("google-chrome", CancellationToken.None)).Name);
    }

    [Fact]
    public void Cancel_with_changes_asks_first()
    {
        var closed = 0;
        var editor = new CatalogEditorViewModel(new DemoAdminApiClient(), Valid(), _ => { }, () => closed++);

        editor.Name = "Renamed";
        Assert.True(editor.IsDirty);
        editor.CancelCommand.Execute(null);
        Assert.True(editor.IsConfirmingDiscard);
        Assert.Equal(0, closed);

        editor.KeepEditingCommand.Execute(null);
        Assert.False(editor.IsConfirmingDiscard);

        editor.Name = Valid().Name;
        editor.CancelCommand.Execute(null);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task The_helpers_fill_the_fields_they_are_for()
    {
        var api = await SignedInDemo();
        var editor = new CatalogEditorViewModel(api, null, _ => { }, () => { });
        editor.Source = CatalogEditorViewModel.SourceDirect;
        editor.DirectUrl = "https://vendor.example/setup.exe";

        await editor.FetchAndHashCommand.ExecuteAsync(null);
        Assert.Equal(64, editor.DirectSha256.Length);
        Assert.Equal("52428800", editor.DirectSizeBytes);
        Assert.Equal("Hash and size filled. Save to keep the definition.", editor.HelperMessage);

        editor.Name = "Zoom";
        await editor.SearchPackagesCommand.ExecuteAsync(null);
        var zoom = Assert.Single(editor.PackageResults);
        editor.UsePackageCommand.Execute(zoom);
        Assert.Equal("Zoom_Workplace", editor.PackageId);

        await editor.VerifyPackageCommand.ExecuteAsync(null);
        Assert.True(editor.VerifyOk);
    }

    [Fact]
    public void The_sentence_follows_the_form()
    {
        var editor = Editor(null);
        editor.Name = "Google Chrome";
        editor.Source = WingetSources.Winget;
        editor.SourceId = "Google.Chrome";

        Assert.Equal("Installs Google Chrome for everyone on the PC, through winget.", editor.Sentence);
    }

    // The web page's sentences, from tests/AppPortal.Server.Tests/CatalogSentenceTests.cs, word for word.

    [Fact]
    public void Sentence_action1()
        => Assert.Equal("Installs Google Chrome for everyone on the PC, through Action1.",
            CatalogEditorViewModel.Describe(App(action1: "Google_Chrome_builtin")));

    [Fact]
    public void Sentence_winget()
        => Assert.Equal("Installs Google Chrome for everyone on the PC, through winget.",
            CatalogEditorViewModel.Describe(App(new WingetPackageDefinition("Google.Chrome", "machine"))));

    [Fact]
    public void Sentence_the_microsoft_store()
        => Assert.Equal("Installs Google Chrome for the person who asks for it, from the Microsoft Store.",
            CatalogEditorViewModel.Describe(App(new WingetPackageDefinition("9WZDNCRFJ3TJ", "user", Source: WingetSources.Store))));

    [Fact]
    public void Sentence_a_package_manager_by_its_own_name()
    {
        Assert.Equal("Installs Google Chrome for everyone on the PC, through Chocolatey.",
            CatalogEditorViewModel.Describe(App(new ManagedPackageDefinition("choco", "googlechrome", "machine"))));
        Assert.Equal("Installs Google Chrome for the person who asks for it, through Scoop.",
            CatalogEditorViewModel.Describe(App(new ManagedPackageDefinition("scoop", "extras/googlechrome", "user"))));
    }

    [Fact]
    public void Sentence_a_direct_download_names_where_it_is_downloaded_from()
        => Assert.Equal("Installs Google Chrome for everyone on the PC, with its own installer from vendor.example.",
            CatalogEditorViewModel.Describe(App(new DirectPackageDefinition("https://vendor.example/installer.exe", new string('a', 64),
                "exe", "/S", 5_000_000_000L, "Vendor Application"))));

    [Fact]
    public void Sentence_both_packages_say_which_device_gets_which_and_what_the_override_decides()
    {
        var both = App(new WingetPackageDefinition("Google.Chrome", "user"), "Google_Chrome_builtin");
        Assert.Equal("Installs Google Chrome for everyone on the PC through Action1, or for the person who asks for it, "
                     + "through winget on a device with only the agent.", CatalogEditorViewModel.Describe(both));

        Assert.EndsWith("A device that could use either uses the agent.",
            CatalogEditorViewModel.Describe(both with { EngineOverride = "agent" }));
    }

    [Fact]
    public void Sentence_an_app_with_no_source_says_nothing_can_install_it()
    {
        Assert.Equal("Google Chrome has no source yet, so no device can install it.", CatalogEditorViewModel.Describe(App()));
        Assert.StartsWith("this app has no source", CatalogEditorViewModel.Describe(new AdminCatalogApp("", "")));
    }

    [Fact]
    public async Task The_list_names_every_source_an_app_has()
    {
        var page = new CatalogViewModel(await SignedInDemo());
        await page.ActivateAsync();

        Assert.Equal("Action1, winget", Row(page, "vscode").Sources);
        Assert.Equal("Chocolatey", Row(page, "7-zip").Sources);
        Assert.Equal("Microsoft Store", Row(page, "whatsapp").Sources);
        Assert.Equal("Direct download", Row(page, "steam").Sources);
    }

    [Fact]
    public async Task The_list_searches_and_filters_on_hidden()
    {
        var page = new CatalogViewModel(await SignedInDemo());
        await page.ActivateAsync();

        page.SearchText = "scoop";
        Assert.Equal("ripgrep", Assert.Single(page.Apps).App.Id);

        page.SearchText = "";
        page.Visibility = 2;
        Assert.Equal("legacy-vpn", Assert.Single(page.Apps).App.Id);
    }

    [Fact]
    public async Task A_delete_refused_for_history_offers_to_hide_instead()
    {
        var page = new CatalogViewModel(await SignedInDemo());
        await page.ActivateAsync();

        page.AskToDelete(Row(page, "google-chrome"));
        Assert.StartsWith("Delete Google Chrome from the catalog?", page.DeletePrompt);
        await page.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Contains("cannot be deleted because installs refer to it", page.ErrorMessage);
        Assert.NotNull(page.RefusedDelete);

        await page.HideInsteadCommand.ExecuteAsync(null);
        Assert.True(Row(page, "google-chrome").Hidden);
        Assert.Equal("'google-chrome' is hidden. Devices are no longer offered it.", page.Notice);
    }

    [Fact]
    public async Task An_app_without_history_is_deleted()
    {
        var page = new CatalogViewModel(await SignedInDemo());
        await page.ActivateAsync();

        page.AskToDelete(Row(page, "steam"));
        await page.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Null(page.ErrorMessage);
        Assert.DoesNotContain(page.Apps, r => r.App.Id == "steam");
    }

    [Fact]
    public async Task A_saved_app_returns_to_the_list_with_its_row_updated()
    {
        var page = new CatalogViewModel(await SignedInDemo());
        await page.ActivateAsync();
        await page.EditAsync(Row(page, "git"));
        Assert.True(page.IsEditing);

        page.Editor!.Source = "choco";
        page.Editor.SourceId = "git";
        await page.Editor.SaveCommand.ExecuteAsync(null);

        Assert.False(page.IsEditing);
        Assert.Equal("Chocolatey", Row(page, "git").Sources);
        Assert.Equal("Saved Git. Devices pick this up on their next refresh.", page.Notice);
    }

    [Fact]
    public async Task Coming_back_to_the_page_keeps_an_open_form()
    {
        var page = new CatalogViewModel(await SignedInDemo());
        await page.ActivateAsync();
        page.NewCommand.Execute(null);
        page.Editor!.Name = "Half typed";

        await page.ActivateAsync();

        Assert.Equal("Half typed", page.Editor?.Name);
    }

    [Fact]
    public async Task Export_then_import_brings_the_catalog_back()
    {
        var api = await SignedInDemo();
        var page = new CatalogViewModel(api) { Files = new Files() };
        await page.ActivateAsync();
        var count = page.Apps.Count;

        await page.ExportCommand.ExecuteAsync(null);
        var file = ((Files)page.Files).Written!;
        Assert.Equal("Exported the catalog to catalog.json.", page.Notice);

        foreach (var row in page.Apps.Where(r => r.App.Id is "steam" or "ripgrep").ToList())
        {
            await api.DeleteCatalogAppAsync(row.App.Id, CancellationToken.None);
        }

        await page.ImportTextAsync(file);

        Assert.Null(page.ErrorMessage);
        Assert.Equal($"Imported {count} apps.", page.Notice);
        Assert.Equal(count, page.Apps.Count);
        Assert.Equal("Direct download", Row(page, "steam").Sources);
    }

    [Fact]
    public async Task A_bad_import_file_shows_why()
    {
        var page = new CatalogViewModel(await SignedInDemo());

        await page.ImportTextAsync("{ not json");

        Assert.StartsWith("That file is not valid JSON.", page.ErrorMessage);
    }

    /// <summary>
    /// The server counts the file in bytes, so the client does too. Counting characters would send a
    /// file of two-byte characters the server is bound to refuse, and show its 413 instead of this.
    /// </summary>
    [Fact]
    public async Task An_import_file_over_the_limit_in_bytes_is_stopped_before_it_is_sent()
    {
        var page = new CatalogViewModel(await SignedInDemo());
        var file = "{ \"apps\": [], \"note\": \"" + new string('é', AdminApiLimits.MaxImportBytes / 2) + "\" }";
        Assert.True(file.Length < AdminApiLimits.MaxImportBytes);

        await page.ImportTextAsync(file);

        Assert.Equal($"A catalog file may be at most {AdminApiLimits.MaxImportBytes / (1024 * 1024)} MB.", page.ErrorMessage);
        Assert.Null(page.Notice);
    }

    // ---- from a request -----------------------------------------------------------------------

    /// <summary>The demo's Slack request, approved, as the Requests page hands it over.</summary>
    private static async Task<AdminRequest> ApprovedSlack(AdminScriptedApi script)
        => await script.Demo.ApproveRequestAsync("req-1", null, null, CancellationToken.None);

    [Fact]
    public async Task A_request_prefills_the_name_and_id_without_making_the_form_dirty()
    {
        var (api, script) = AdminScriptedApi.Create();
        var closed = false;
        var editor = new CatalogEditorViewModel(api, null, _ => { }, () => closed = true, await ApprovedSlack(script));

        Assert.Equal("Slack", editor.Name);
        Assert.Equal("slack", editor.Id);
        Assert.True(editor.HasRequest);
        Assert.Equal(
            @"For the request from CONTOSO\alee on RECEPTION-01: Slack, for the new support rota. "
            + "The name and id are suggested from it, so check both. Saving links the request to this app.",
            editor.RequestBanner);
        Assert.False(editor.IsDirty);

        editor.CancelCommand.Execute(null);
        Assert.True(closed);
        Assert.False(editor.IsConfirmingDiscard);
    }

    [Fact]
    public async Task Saving_links_the_request_to_the_stored_app()
    {
        var (api, script) = AdminScriptedApi.Create();
        AdminCatalogApp? stored = null;
        var editor = new CatalogEditorViewModel(api, null, saved => stored = saved, () => { }, await ApprovedSlack(script))
        {
            PackageId = "Slack_Slack_builtin",
        };

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(editor.ErrorMessage);
        Assert.Equal("slack", stored!.Id);
        var args = script.Last(nameof(IAdminApiClient.LinkRequestAsync));
        Assert.Equal(("req-1", "slack"), ((string)args[0]!, (string?)args[1]));
        Assert.Null(editor.LinkError);
        var request = (await script.Demo.GetRequestsAsync(AppRequestStatus.Approved, 0, 50, CancellationToken.None)).Items.Single(r => r.Id == "req-1");
        Assert.Equal(("slack", "Slack"), (request.CatalogAppId, request.CatalogAppName));
    }

    [Fact]
    public async Task A_link_the_server_refuses_leaves_the_app_saved_with_the_reason()
    {
        var (api, script) = AdminScriptedApi.Create();
        script.On(nameof(IAdminApiClient.LinkRequestAsync), _ => Task.FromException<AdminRequest>(
            new PortalApiException("Only an approved request can name a catalog app.", HttpStatusCode.Conflict)));
        AdminCatalogApp? stored = null;
        var editor = new CatalogEditorViewModel(api, null, saved => stored = saved, () => { }, await ApprovedSlack(script))
        {
            PackageId = "Slack_Slack_builtin",
        };

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(stored);
        Assert.Equal("Only an approved request can name a catalog app.", editor.LinkError);
        Assert.Null(editor.ErrorMessage);
        Assert.NotNull(await api.GetCatalogAppAsync("slack", CancellationToken.None));
    }

    [Fact]
    public async Task Editing_an_existing_app_ignores_the_request()
    {
        var (api, script) = AdminScriptedApi.Create();
        var existing = await api.GetCatalogAppAsync("vscode", CancellationToken.None);
        var editor = new CatalogEditorViewModel(api, existing, _ => { }, () => { }, await ApprovedSlack(script));

        Assert.False(editor.HasRequest);
        Assert.Null(editor.FromRequest);
        Assert.Equal("Visual Studio Code", editor.Name);

        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, script.Count(nameof(IAdminApiClient.LinkRequestAsync)));
    }

    [Fact]
    public async Task The_catalog_page_says_whether_the_request_was_linked()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new CatalogViewModel(api);
        page.NewFromRequest(await ApprovedSlack(script));
        await WaitUntil(() => !page.IsBusy && page.Apps.Count > 0);
        page.Editor!.PackageId = "Slack_Slack_builtin";

        await page.Editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(page.Editor);
        Assert.Equal(@"Saved Slack and linked it to the request from CONTOSO\alee. Devices pick this up on their next refresh.", page.Notice);
        Assert.Null(page.ErrorMessage);

        script.On(nameof(IAdminApiClient.LinkRequestAsync), _ => Task.FromException<AdminRequest>(
            new PortalApiException("Only an approved request can name a catalog app.", HttpStatusCode.Conflict)));
        var blender = await script.Demo.ApproveRequestAsync("req-2", null, null, CancellationToken.None);
        page.NewFromRequest(blender);
        page.Editor!.PackageId = "Blender_builtin";
        await page.Editor.SaveCommand.ExecuteAsync(null);

        Assert.Equal("Saved Blender.", page.Notice);
        Assert.Equal("The request could not be linked. Only an approved request can name a catalog app.", page.ErrorMessage);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not come true in time.");
            await Task.Delay(10);
        }
    }

    private static CatalogEditorViewModel Editor(AdminCatalogApp? app)
        => new(new DemoAdminApiClient(), app, _ => { }, () => { });

    private static AdminCatalogApp Valid() => new("app", "App", Requires: [], Action1: new AdminAction1Package("Vendor_App"));

    private static AdminCatalogApp App(PackageDefinition? agent = null, string action1 = "")
        => new("app", "Google Chrome", Action1: new AdminAction1Package(action1), Agent: agent);

    private static CatalogRowViewModel Row(CatalogViewModel page, string id) => page.Apps.Single(r => r.App.Id == id);

    /// <summary>Records holding lists compare by reference, so two apps are compared by what they would send.</summary>
    private static string Text(AdminCatalogApp app) => JsonSerializer.Serialize(app, Json);

    private static async Task<DemoAdminApiClient> SignedInDemo()
    {
        var api = new DemoAdminApiClient();
        api.Token = (await api.SignInAsync(DemoAdminApiClient.DemoUsername, DemoAdminApiClient.DemoPassword, null, CancellationToken.None)).Token;
        return api;
    }

    /// <summary>The real client over a handler that answers every call with <paramref name="status"/> and <paramref name="body"/>.</summary>
    private static AdminApiClient Api(HttpStatusCode status, string body)
        => new("https://portal.example", new Reply(status, body)) { Token = "apa_test" };

    private sealed class Reply(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private sealed class Files : ICatalogFiles
    {
        public string? Written { get; private set; }

        public Task<string?> OpenAsync() => Task.FromResult<string?>(null);

        public Task<string?> SaveAsync(string json)
        {
            Written = json;
            return Task.FromResult<string?>("catalog.json");
        }
    }
}
