using System.Net;

using AppPortal.Client.Services;
using AppPortal.Client.ViewModels;
using AppPortal.Client.ViewModels.Admin;
using AppPortal.Shared;

namespace AppPortal.Client.Tests;

public sealed class AdminRequestsTests
{
    [Fact]
    public async Task The_page_opens_on_the_pending_queue_with_its_count()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new RequestsViewModel(api);

        await page.ActivateAsync();

        Assert.Equal("Pending", page.SelectedTab.Label);
        Assert.Equal(AppRequestStatus.Pending, LastList(script)[0]);
        Assert.Equal(3, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.True(r.CanDecide));
        Assert.Equal(page.Rows.OrderByDescending(r => r.Request.CreatedAt).Select(r => r.Request.Id), page.Rows.Select(r => r.Request.Id));
        Assert.Equal(3, page.PendingCount);
        Assert.True(page.HasPending);
        Assert.False(page.HasPager);
        Assert.Null(page.ErrorMessage);
    }

    [Fact]
    public async Task Each_tab_shows_its_own_status_and_All_shows_every_request()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();

        page.SelectedTab = RequestsViewModel.Tabs.Single(t => t.Label == "Approved");
        await WaitUntil(() => !page.IsBusy);
        Assert.Equal(2, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.True(r.IsApproved));
        Assert.All(page.Rows, r => Assert.False(r.CanDecide));
        Assert.Contains(page.Rows, r => r.ReasonText == "No reason given." && r.RequesterText == "unknown");

        page.SelectedTab = RequestsViewModel.Tabs.Single(t => t.Label == "Denied");
        await WaitUntil(() => !page.IsBusy);
        Assert.Equal(2, page.Rows.Count);
        Assert.All(page.Rows, r => Assert.True(r.IsDenied));

        page.SelectedTab = RequestsViewModel.Tabs.Single(t => t.Label == "All");
        await WaitUntil(() => !page.IsBusy);
        Assert.Null(LastList(script)[0]);
        Assert.Equal(7, page.Rows.Count);
    }

    [Fact]
    public async Task Approving_with_a_reason_takes_the_request_off_the_pending_tab()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();
        var row = page.Rows.Single(r => r.Request.Id == "req-1");

        row.ApproveCommand.Execute(null);
        Assert.True(page.IsDecisionOpen);
        Assert.Same(row, page.Deciding);
        Assert.Equal("Approve this request?", page.DecisionTitle);
        Assert.Equal("Approve", page.DecisionButtonText);

        page.Reason = "  Added to the catalog, it appears within the hour. ";
        await page.ConfirmDecisionCommand.ExecuteAsync(null);

        Assert.False(page.IsDecisionOpen);
        Assert.DoesNotContain(row, page.Rows);
        Assert.Equal(2, page.PendingCount);
        Assert.Equal("Request approved.", page.Notice);
        Assert.Null(page.ErrorMessage);
        var args = script.Last(nameof(IAdminApiClient.ApproveRequestAsync));
        Assert.Equal("req-1", args[0]);
        Assert.Equal("Added to the catalog, it appears within the hour.", args[1]);

        // What the web page and the requesting device read from now on.
        var recorded = (await script.Demo.GetRequestsAsync(AppRequestStatus.Approved, 0, 50, CancellationToken.None)).Items.Single(r => r.Id == "req-1");
        Assert.Equal("Added to the catalog, it appears within the hour.", recorded.Reason);
        Assert.Equal(DemoAdminApiClient.DemoUsername, recorded.DecidedBy);
    }

    [Fact]
    public async Task Denying_without_a_reason_sends_none()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();
        var row = page.Rows.Single(r => r.Request.Id == "req-2");

        page.DenyCommand.Execute(row);
        Assert.Equal("Deny this request?", page.DecisionTitle);
        page.Reason = "   ";
        await page.ConfirmDecisionCommand.ExecuteAsync(null);

        Assert.Null(script.Last(nameof(IAdminApiClient.DenyRequestAsync))[1]);
        Assert.Equal("Request denied.", page.Notice);
        Assert.Equal(0, script.Count(nameof(IAdminApiClient.ApproveRequestAsync)));
    }

    [Fact]
    public async Task Cancelling_the_dialog_decides_nothing()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();

        page.ApproveCommand.Execute(page.Rows[0]);
        page.Reason = "Changed my mind";
        page.CancelDecisionCommand.Execute(null);

        Assert.False(page.IsDecisionOpen);
        Assert.Equal("", page.Reason);
        Assert.Equal(3, page.Rows.Count);
        Assert.Equal(0, script.Count(nameof(IAdminApiClient.ApproveRequestAsync)));
    }

    [Fact]
    public async Task A_decision_shows_at_once_and_goes_back_if_the_server_refuses_it()
    {
        var (api, script) = AdminScriptedApi.Create();
        var answer = new TaskCompletionSource<AdminRequest>();
        script.On(nameof(IAdminApiClient.ApproveRequestAsync), _ => answer.Task);
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();
        var row = page.Rows[1];
        var before = row.Request;

        page.ApproveCommand.Execute(row);
        var confirming = page.ConfirmDecisionCommand.ExecuteAsync(null);

        // Before the server has answered: gone from the queue, and counted as gone.
        Assert.DoesNotContain(row, page.Rows);
        Assert.Equal(2, page.PendingCount);
        Assert.True(row.IsSaving);

        answer.SetException(new PortalApiException("A reason may be at most 500 characters.", HttpStatusCode.BadRequest));
        await confirming;

        Assert.Same(row, page.Rows[1]);
        Assert.Equal(before, row.Request);
        Assert.False(row.IsSaving);
        Assert.True(row.CanDecide);
        Assert.Equal(3, page.PendingCount);
        Assert.Equal("A reason may be at most 500 characters.", page.ErrorMessage);
        Assert.Null(page.Notice);
    }

    [Fact]
    public async Task On_the_All_tab_the_decision_is_shown_on_the_row_while_it_saves()
    {
        var (api, script) = AdminScriptedApi.Create();
        var answer = new TaskCompletionSource<AdminRequest>();
        script.On(nameof(IAdminApiClient.DenyRequestAsync), _ => answer.Task);
        var page = new RequestsViewModel(api);
        page.SelectedTab = RequestsViewModel.Tabs[3];
        await WaitUntil(() => !page.IsBusy && page.Rows.Count == 7);
        var row = page.Rows.Single(r => r.Request.Id == "req-5");

        page.DenyCommand.Execute(row);
        page.Reason = "Use the web version.";
        var confirming = page.ConfirmDecisionCommand.ExecuteAsync(null);

        Assert.Contains(row, page.Rows);
        Assert.True(row.IsDenied);
        Assert.Equal("Use the web version.", row.ReasonText);
        Assert.Equal("Saving…", row.DecidedText);
        Assert.False(row.CanDecide);

        answer.SetResult(await script.Demo.DenyRequestAsync("req-5", "Use the web version.", CancellationToken.None));
        await confirming;

        Assert.StartsWith("by admin on ", row.DecidedText);
        Assert.Equal("Request denied.", page.Notice);
    }

    [Fact]
    public async Task A_request_somebody_else_decided_first_is_shown_as_they_decided_it()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();
        var row = page.Rows.Single(r => r.Request.Id == "req-2");

        // Another administrator, on the web, a moment earlier.
        await script.Demo.DenyRequestAsync("req-2", "No licence.", CancellationToken.None);
        page.ApproveCommand.Execute(row);
        await page.ConfirmDecisionCommand.ExecuteAsync(null);

        Assert.Equal("That request had already been decided. The list now shows the decision that was recorded.", page.ErrorMessage);
        Assert.DoesNotContain(page.Rows, r => r.Request.Id == "req-2");
        Assert.Equal(2, page.PendingCount);
        Assert.Null(page.Notice);
    }

    [Fact]
    public async Task Next_and_previous_step_through_the_list_a_page_at_a_time()
    {
        var (api, script) = AdminScriptedApi.Create();
        script.On(nameof(IAdminApiClient.GetRequestsAsync), args =>
        {
            var offset = (int)args[1]!;
            var limit = (int)args[2]!;
            var rows = Enumerable.Range(offset, Math.Min(limit, 120 - offset)).Select(i => Request("req-" + i)).ToList();
            return Task.FromResult(new AdminPage<AdminRequest>(rows, offset, limit, offset + rows.Count < 120, 120));
        });
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();

        Assert.Equal("Page 1", page.PageText);
        Assert.True(page.HasPager);
        Assert.False(page.PreviousCommand.CanExecute(null));

        await page.NextCommand.ExecuteAsync(null);
        await page.NextCommand.ExecuteAsync(null);
        Assert.Equal(100, LastList(script)[1]);
        Assert.Equal("Page 3", page.PageText);
        Assert.Equal(20, page.Rows.Count);
        Assert.False(page.NextCommand.CanExecute(null));

        await page.PreviousCommand.ExecuteAsync(null);
        Assert.Equal("Page 2", page.PageText);
        Assert.Equal(RequestsViewModel.PageSize, page.Rows.Count);
    }

    [Fact]
    public async Task A_list_that_cannot_be_read_says_why()
    {
        var (api, script) = AdminScriptedApi.Create();
        script.On(nameof(IAdminApiClient.GetRequestsAsync), _ => Task.FromException<AdminPage<AdminRequest>>(
            new PortalApiException("The App Portal server did not answer in time.")));
        var page = new RequestsViewModel(api);

        await page.ActivateAsync();

        Assert.Equal("The App Portal server did not answer in time.", page.ErrorMessage);
        Assert.False(page.IsBusy);
        Assert.False(page.HasRows);
    }

    [Fact]
    public async Task The_pending_badge_is_counted_before_the_requests_page_is_opened()
    {
        var model = Model(out _);

        await model.SignInAdminAsync("admin", "demo");

        Assert.Equal(AdminSections.Dashboard, model.SelectedSection);
        Assert.Equal(3, model.AdminArea!.Requests.PendingCount);
        Assert.True(model.AdminArea.Requests.HasPending);
    }

    [Fact]
    public async Task A_session_refused_on_a_decision_signs_out_with_a_notice()
    {
        var model = Model(out var session);
        await model.SignInAdminAsync("admin", "demo");
        model.SelectedSection = AdminSections.Requests;
        var page = model.AdminArea!.Requests;

        page.ApproveCommand.Execute(page.Rows[0]);
        session.Api.Token = "apa_revoked";
        await page.ConfirmDecisionCommand.ExecuteAsync(null);

        Assert.False(model.IsAdminSignedIn);
        Assert.Equal(0, model.SelectedSection);
        Assert.Equal(AdminApiClient.SessionEndedMessage, model.AdminNotice);
    }

    [Fact]
    public async Task Approving_sends_the_catalog_app_chosen_in_the_dialog()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();
        var row = page.Rows.Single(r => r.Request.Id == "req-1");

        page.ApproveCommand.Execute(row);
        await WaitUntil(() => page.CatalogChoices.Count > 1);
        Assert.Same(RequestsViewModel.NotInCatalog, page.CatalogChoices[0]);
        Assert.Equal("Not in the catalog yet", page.CatalogChoices[0].Label);
        Assert.Same(RequestsViewModel.NotInCatalog, page.SelectedCatalogApp);
        Assert.Contains(page.CatalogChoices, c => c is { Id: "legacy-vpn", Label: "Legacy VPN client (hidden)" });

        page.SelectedCatalogApp = page.CatalogChoices.Single(c => c.Id == "vscode");
        await page.ConfirmDecisionCommand.ExecuteAsync(null);

        var args = script.Last(nameof(IAdminApiClient.ApproveRequestAsync));
        Assert.Equal("req-1", args[0]);
        Assert.Equal("vscode", args[2]);
        var recorded = (await script.Demo.GetRequestsAsync(AppRequestStatus.Approved, 0, 50, CancellationToken.None)).Items.Single(r => r.Id == "req-1");
        Assert.Equal("vscode", recorded.CatalogAppId);
        Assert.Equal("Visual Studio Code", recorded.CatalogAppName);
    }

    [Fact]
    public async Task Approving_with_no_app_chosen_sends_none()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();

        page.ApproveCommand.Execute(page.Rows[0]);
        await WaitUntil(() => page.CatalogChoices.Count > 1);
        await page.ConfirmDecisionCommand.ExecuteAsync(null);

        Assert.Null(script.Last(nameof(IAdminApiClient.ApproveRequestAsync))[2]);
    }

    [Fact]
    public async Task A_catalog_that_cannot_be_read_leaves_only_the_first_choice()
    {
        var (api, script) = AdminScriptedApi.Create();
        script.On(nameof(IAdminApiClient.GetCatalogAsync), _ => Task.FromException<AdminPage<AdminCatalogApp>>(
            new PortalApiException("The App Portal server did not answer in time.")));
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();

        page.ApproveCommand.Execute(page.Rows[0]);
        await WaitUntil(() => page.ErrorMessage is not null);

        Assert.Equal([RequestsViewModel.NotInCatalog], page.CatalogChoices);
        Assert.Equal("The App Portal server did not answer in time.", page.ErrorMessage);
        Assert.True(page.IsDecisionOpen);
    }

    [Fact]
    public async Task Approve_and_add_calls_back_only_after_the_server_accepts()
    {
        var (api, script) = AdminScriptedApi.Create();
        var answer = new TaskCompletionSource<AdminRequest>();
        script.On(nameof(IAdminApiClient.ApproveRequestAsync), _ => answer.Task);
        var added = new List<AdminRequest>();
        var page = new RequestsViewModel(api, added.Add);
        await page.ActivateAsync();
        var row = page.Rows.Single(r => r.Request.Id == "req-1");

        page.ApproveCommand.Execute(row);
        page.Reason = "Adding it now.";
        var approving = page.ApproveAndAddCommand.ExecuteAsync(null);

        Assert.DoesNotContain(row, page.Rows);
        Assert.Empty(added);

        answer.SetResult(await script.Demo.ApproveRequestAsync("req-1", "Adding it now.", null, CancellationToken.None));
        await approving;

        var request = Assert.Single(added);
        Assert.Equal("req-1", request.Id);
        Assert.Equal(AppRequestStatus.Approved, request.Status);
        var args = script.Last(nameof(IAdminApiClient.ApproveRequestAsync));
        Assert.Equal("Adding it now.", args[1]);
        Assert.Null(args[2]);
    }

    [Fact]
    public async Task Approve_and_add_does_not_call_back_when_the_server_refuses()
    {
        var (api, script) = AdminScriptedApi.Create();
        var added = new List<AdminRequest>();
        var page = new RequestsViewModel(api, added.Add);
        await page.ActivateAsync();
        var row = page.Rows.Single(r => r.Request.Id == "req-2");
        await script.Demo.DenyRequestAsync("req-2", "No licence.", CancellationToken.None);

        page.ApproveCommand.Execute(row);
        await page.ApproveAndAddCommand.ExecuteAsync(null);

        Assert.Empty(added);
        Assert.StartsWith("That request had already been decided.", page.ErrorMessage);
    }

    [Fact]
    public async Task Approved_rows_show_the_link_and_the_buttons_that_fit_it()
    {
        var (api, script) = AdminScriptedApi.Create();
        await script.Demo.ApproveRequestAsync("req-1", null, "legacy-vpn", CancellationToken.None);
        await script.Demo.ApproveRequestAsync("req-2", null, "ripgrep", CancellationToken.None);
        await script.Demo.DeleteCatalogAppAsync("ripgrep", CancellationToken.None);
        var page = new RequestsViewModel(api);
        page.SelectedTab = RequestsViewModel.Tabs.Single(t => t.Label == "Approved");
        await WaitUntil(() => !page.IsBusy && page.Rows.Count == 4);

        var linked = page.Rows.Single(r => r.Request.Id == "req-3");
        Assert.Equal("Catalog app: Notepad++", linked.CatalogAppText);
        Assert.True(linked.HasCatalogApp);
        Assert.False(linked.CanAddToCatalog);
        Assert.True(linked.CanLink);

        var unlinked = page.Rows.Single(r => r.Request.Id == "req-6");
        Assert.Equal("", unlinked.CatalogAppText);
        Assert.False(unlinked.HasCatalogApp);
        Assert.True(unlinked.CanAddToCatalog);
        Assert.True(unlinked.CanLink);

        Assert.Equal("Catalog app: Legacy VPN client (hidden)", page.Rows.Single(r => r.Request.Id == "req-1").CatalogAppText);
        Assert.Equal("Catalog app: 'ripgrep', no longer in the catalog", page.Rows.Single(r => r.Request.Id == "req-2").CatalogAppText);

        page.SelectedTab = RequestsViewModel.Tabs.Single(t => t.Label == "Denied");
        await WaitUntil(() => !page.IsBusy && page.Rows.All(r => r.IsDenied));
        Assert.All(page.Rows, r => Assert.False(r.CanLink || r.CanAddToCatalog));
    }

    [Fact]
    public async Task Add_to_the_catalog_on_a_row_calls_back_with_its_request()
    {
        var (api, _) = AdminScriptedApi.Create();
        var added = new List<AdminRequest>();
        var page = new RequestsViewModel(api, added.Add);
        page.SelectedTab = RequestsViewModel.Tabs.Single(t => t.Label == "Approved");
        await WaitUntil(() => !page.IsBusy && page.Rows.Count == 2);

        page.Rows.Single(r => r.Request.Id == "req-6").AddToCatalogCommand.Execute(null);

        Assert.Equal("req-6", Assert.Single(added).Id);
    }

    [Fact]
    public async Task The_link_dialog_sends_the_chosen_app_or_null_to_remove_it()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new RequestsViewModel(api);
        page.SelectedTab = RequestsViewModel.Tabs.Single(t => t.Label == "Approved");
        await WaitUntil(() => !page.IsBusy && page.Rows.Count == 2);
        var row = page.Rows.Single(r => r.Request.Id == "req-6");

        row.LinkCommand.Execute(null);
        Assert.True(page.IsLinkOpen);
        Assert.Same(row, page.Linking);
        await WaitUntil(() => page.CatalogChoices.Count > 1);
        Assert.Same(RequestsViewModel.NotInCatalog, page.SelectedCatalogApp);
        page.SelectedCatalogApp = page.CatalogChoices.Single(c => c.Id == "vscode");
        await page.ConfirmLinkCommand.ExecuteAsync(null);

        Assert.False(page.IsLinkOpen);
        var args = script.Last(nameof(IAdminApiClient.LinkRequestAsync));
        Assert.Equal(("req-6", "vscode"), ((string)args[0]!, (string?)args[1]));
        Assert.Equal("Catalog app: Visual Studio Code", row.CatalogAppText);
        Assert.Equal("Linked to Visual Studio Code.", page.Notice);

        // Opened again, the dialog starts on the app the request names now.
        row.LinkCommand.Execute(null);
        await WaitUntil(() => page.CatalogChoices.Count > 1);
        Assert.Equal("vscode", page.SelectedCatalogApp?.Id);
        page.SelectedCatalogApp = RequestsViewModel.NotInCatalog;
        await page.ConfirmLinkCommand.ExecuteAsync(null);

        Assert.Null(script.Last(nameof(IAdminApiClient.LinkRequestAsync))[1]);
        Assert.Equal("", row.CatalogAppText);
        Assert.True(row.CanAddToCatalog);
        Assert.Equal("The request no longer names a catalog app.", page.Notice);
    }

    [Fact]
    public async Task A_refused_link_leaves_the_row_and_says_why()
    {
        var (api, script) = AdminScriptedApi.Create();
        script.On(nameof(IAdminApiClient.LinkRequestAsync), _ => Task.FromException<AdminRequest>(
            new PortalApiException("No app with id 'vscode' is in the catalog.", HttpStatusCode.UnprocessableEntity)));
        var page = new RequestsViewModel(api);
        page.SelectedTab = RequestsViewModel.Tabs.Single(t => t.Label == "Approved");
        await WaitUntil(() => !page.IsBusy && page.Rows.Count == 2);
        var row = page.Rows.Single(r => r.Request.Id == "req-3");

        row.LinkCommand.Execute(null);
        await WaitUntil(() => page.CatalogChoices.Count > 1);
        Assert.Equal("notepadpp", page.SelectedCatalogApp?.Id);
        page.SelectedCatalogApp = page.CatalogChoices.Single(c => c.Id == "vscode");
        await page.ConfirmLinkCommand.ExecuteAsync(null);

        Assert.Equal("No app with id 'vscode' is in the catalog.", page.ErrorMessage);
        Assert.Equal("Catalog app: Notepad++", row.CatalogAppText);
        Assert.False(row.IsSaving);
    }

    [Fact]
    public async Task A_pending_request_has_no_link_dialog()
    {
        var (api, script) = AdminScriptedApi.Create();
        var page = new RequestsViewModel(api);
        await page.ActivateAsync();
        var row = page.Rows[0];

        Assert.False(row.CanLink);
        Assert.False(row.CanAddToCatalog);
        row.LinkCommand.Execute(null);

        Assert.False(page.IsLinkOpen);
        Assert.Equal(0, script.Count(nameof(IAdminApiClient.LinkRequestAsync)));
    }

    [Fact]
    public async Task The_demo_refuses_an_unknown_app_and_a_link_on_a_request_that_is_not_approved()
    {
        var (_, script) = AdminScriptedApi.Create();

        var unknown = await Assert.ThrowsAsync<PortalApiException>(() => script.Demo.ApproveRequestAsync("req-1", null, "nothing", CancellationToken.None));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.Status);
        var pending = await Assert.ThrowsAsync<PortalApiException>(() => script.Demo.LinkRequestAsync("req-1", "vscode", CancellationToken.None));
        Assert.Equal(HttpStatusCode.Conflict, pending.Status);

        var linked = await script.Demo.LinkRequestAsync("req-3", "VSCODE", CancellationToken.None);
        Assert.Equal(("vscode", "Visual Studio Code"), (linked.CatalogAppId, linked.CatalogAppName));
    }

    /// <summary>The last read of the list itself, as opposed to the one-row read that counts the badge.</summary>
    private static object?[] LastList(AdminScriptedApi script)
        => script.All(nameof(IAdminApiClient.GetRequestsAsync)).Last(a => (int)a[2]! == RequestsViewModel.PageSize);

    private static MainViewModel Model(out AdminSession session)
    {
        session = new AdminSession(new DemoAdminApiClient(), NoAdminSessionStore.Instance, "demo");
        return new MainViewModel(null, new ClientSettings(), isDemo: true, session);
    }

    private static AdminRequest Request(string id) => new(id, "Something useful", "RECEPTION-01", @"CONTOSO\alee",
        AppRequestStatus.Pending, null, null, DateTimeOffset.Now, null);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not come true in time.");
            await Task.Delay(10);
        }
    }
}
