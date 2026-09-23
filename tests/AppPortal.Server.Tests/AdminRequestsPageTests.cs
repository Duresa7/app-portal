using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Requests;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class AdminRequestsPageTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly AppRequestStore _requests;
    private readonly string _deviceToken;

    public AdminRequestsPageTests()
    {
        var catalogPath = Path.Combine(_test.Root, "catalog.json");
        File.WriteAllText(catalogPath, """{ "apps": [] }""");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:CatalogPath", catalogPath);
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });

        _test.AddAdmin();
        _deviceToken = new DeviceStore(_test.Database).Add("TESTPC", "endpoint-1234");
        _requests = new AppRequestStore(_test.Database);
    }

    private Task<HttpClient> SignedIn() => TestDatabase.SignedIn(_factory);

    private static Task<string> TokenFrom(HttpClient client, string path) => TestDatabase.TokenOn(client, path);

    /// <summary>
    /// Razor's encoder escapes more than the five XML characters: "Notepad++" reaches the page as
    /// "Notepad&#x2B;&#x2B;". Decoding once here lets every assertion below read like the screen does.
    /// </summary>
    private static async Task<string> Html(HttpClient client, string url)
        => WebUtility.HtmlDecode(await client.GetStringAsync(url));

    private static async Task<string> Body(HttpResponseMessage response)
        => WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private async Task<HttpResponseMessage> Decide(
        HttpClient client, string handler, string id, string? reason, bool htmx, string? catalogAppId = null)
    {
        var token = await TokenFrom(client, "/admin/requests");
        var fields = new Dictionary<string, string>
        {
            ["id"] = id,
            ["tab"] = "pending",
            ["p"] = "1",
            ["__RequestVerificationToken"] = token,
        };
        if (reason is not null)
        {
            fields["reason"] = reason;
        }

        if (catalogAppId is not null)
        {
            fields["catalogAppId"] = catalogAppId;
        }

        var message = new HttpRequestMessage(HttpMethod.Post, $"/admin/requests?handler={handler}")
        {
            Content = new FormUrlEncodedContent(fields),
        };
        if (htmx)
        {
            message.Headers.Add("HX-Request", "true");
        }

        return await client.SendAsync(message);
    }

    [Fact]
    public async Task The_page_is_closed_to_anyone_not_signed_in()
    {
        var response = await TestDatabase.Browser(_factory).GetAsync("/admin/requests");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_filed_from_a_device_shows_up_under_pending()
    {
        var device = _factory.CreateClient();
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _deviceToken);
        device.DefaultRequestHeaders.Add(ApiHeaders.Requester, @"CONTOSO\jdoe");
        var created = await device.PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest("Notepad++"), Json);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var response = await (await SignedIn()).GetAsync("/admin/requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Notepad++", html, StringComparison.Ordinal);
        Assert.Contains("TESTPC", html, StringComparison.Ordinal);
        Assert.Contains(@"CONTOSO\jdoe", html, StringComparison.Ordinal);
        Assert.Contains("Pending", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_records_the_decision_the_reason_and_who_made_it()
    {
        var request = _requests.Create("TESTPC", @"CONTOSO\jdoe", "Notepad++");
        var client = await SignedIn();

        var response = await Decide(client, "Approve", request.Id, "Added to the catalog.", htmx: true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = _requests.Find(request.Id)!;
        Assert.Equal(AppRequestStatus.Approved, stored.Status);
        Assert.Equal("Added to the catalog.", stored.Reason);
        Assert.Equal("admin", stored.DecidedBy);
        Assert.NotNull(stored.DecidedAt);
    }

    [Fact]
    public async Task Denying_without_a_reason_stores_no_reason_rather_than_an_empty_one()
    {
        var request = _requests.Create("TESTPC", null, "A licence for Acrobat");
        var client = await SignedIn();

        await Decide(client, "Deny", request.Id, "   ", htmx: true);

        var stored = _requests.Find(request.Id)!;
        Assert.Equal(AppRequestStatus.Denied, stored.Status);
        Assert.Null(stored.Reason);
    }

    [Fact]
    public async Task An_htmx_decision_answers_with_the_table_and_the_new_badge_count()
    {
        var first = _requests.Create("TESTPC", null, "Notepad++");
        _requests.Create("TESTPC", null, "Slack");
        var client = await SignedIn();

        var response = await Decide(client, "Approve", first.Id, "Fine.", htmx: true);
        var body = await Body(response);

        // The table comes back so the row can be replaced in place, and the badge rides along out of band.
        Assert.Contains("id=\"request-table\"", body, StringComparison.Ordinal);
        Assert.Contains("id=\"pending-badge\"", body, StringComparison.Ordinal);
        Assert.Contains("hx-swap-oob=\"true\"", body, StringComparison.Ordinal);
        Assert.Contains("Request approved.", body, StringComparison.Ordinal);

        // One of the two is decided, so the badge must now read 1, not 2.
        var badge = Regex.Match(body, "id=\"pending-badge\"[^>]*>\\s*(\\d+)\\s*<");
        Assert.True(badge.Success, "The out-of-band badge carried no count.");
        Assert.Equal("1", badge.Groups[1].Value);

        // It is a fragment, not a page: no layout came back with it.
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_decision_without_htmx_comes_back_as_a_whole_page()
    {
        var request = _requests.Create("TESTPC", null, "Notepad++");
        var client = await SignedIn();

        var response = await Decide(client, "Approve", request.Id, "Fine.", htmx: false);
        var body = await Body(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Request approved.", body, StringComparison.Ordinal);
        Assert.Equal(AppRequestStatus.Approved, _requests.Find(request.Id)!.Status);
    }

    [Fact]
    public async Task A_second_administrator_cannot_decide_the_same_request_again()
    {
        var request = _requests.Create("TESTPC", null, "Notepad++");
        var client = await SignedIn();

        await Decide(client, "Approve", request.Id, "First one wins.", htmx: true);
        var second = await Decide(client, "Deny", request.Id, "Second one should not.", htmx: true);
        var body = await Body(second);

        Assert.Contains("had already been decided", body, StringComparison.Ordinal);
        Assert.Contains("message error", body, StringComparison.Ordinal);

        var stored = _requests.Find(request.Id)!;
        Assert.Equal(AppRequestStatus.Approved, stored.Status);
        Assert.Equal("First one wins.", stored.Reason);
    }

    [Fact]
    public async Task A_reason_past_the_limit_is_refused_and_nothing_is_decided()
    {
        var request = _requests.Create("TESTPC", null, "Notepad++");
        var client = await SignedIn();

        var response = await Decide(client, "Approve", request.Id, new string('x', AppRequestLimits.MaxTextLength + 1), htmx: true);
        var body = await Body(response);

        Assert.Contains("at most", body, StringComparison.Ordinal);
        Assert.Equal(AppRequestStatus.Pending, _requests.Find(request.Id)!.Status);
    }

    [Fact]
    public async Task The_table_pages_at_fifty_and_the_second_page_holds_the_rest()
    {
        // A device may hold only AppRequestLimits.MaxPendingPerDevice undecided requests, so two pages
        // of them have to come from more than one machine.
        var devices = new DeviceStore(_test.Database);
        var names = new[] { "PAGEPC1", "PAGEPC2", "PAGEPC3" };
        foreach (var name in names)
        {
            devices.Add(name, $"endpoint-{name}");
        }

        for (var i = 0; i < 55; i++)
        {
            _requests.Create(names[i / AppRequestLimits.MaxPendingPerDevice], null, $"Request number {i:D2}");
        }

        var client = await SignedIn();

        var first = await Html(client, "/admin/requests?tab=pending");
        Assert.Equal(50, Regex.Matches(first, "<tr id=\"request-").Count);
        Assert.Contains("Next", first, StringComparison.Ordinal);

        var second = await Html(client, "/admin/requests?tab=pending&p=2");
        Assert.Equal(5, Regex.Matches(second, "<tr id=\"request-").Count);
        Assert.Contains("Previous", second, StringComparison.Ordinal);
        Assert.DoesNotContain(">Next<", second, StringComparison.Ordinal);

        // Newest first, so the highest-numbered request is on page one and the lowest on page two.
        Assert.Contains("Request number 54", first, StringComparison.Ordinal);
        Assert.Contains("Request number 00", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_the_fifty_first_pending_request_removes_the_next_page_link()
    {
        var devices = new DeviceStore(_test.Database);
        var names = new[] { "PAGEPC1", "PAGEPC2", "PAGEPC3" };
        foreach (var name in names)
        {
            devices.Add(name, $"endpoint-{name}");
        }

        AppRequestRecord? newest = null;
        for (var i = 0; i < 51; i++)
        {
            newest = _requests.Create(names[i / AppRequestLimits.MaxPendingPerDevice], null, $"Request number {i:D2}");
        }

        var client = await SignedIn();
        var before = await Html(client, "/admin/requests?tab=pending");
        Assert.Contains("id=\"request-pager\"", before, StringComparison.Ordinal);
        Assert.Contains(">Next<", before, StringComparison.Ordinal);

        var response = await Decide(client, "Approve", newest!.Id, "Approved.", htmx: true);
        var body = await Body(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(50, Regex.Matches(body, "<tr id=\"request-").Count);
        Assert.Matches("<div id=\"request-pager\" hx-swap-oob=\"true\">\\s*</div>", body);
        Assert.DoesNotContain(">Next<", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);

        var after = await Html(client, "/admin/requests?tab=pending");
        Assert.Contains("id=\"request-pager\"", after, StringComparison.Ordinal);
        Assert.DoesNotContain(">Next<", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dashboard_counts_what_is_still_waiting()
    {
        var decided = _requests.Create("TESTPC", null, "Already answered");
        _requests.Create("TESTPC", null, "Still waiting");
        Assert.True(_requests.Decide(decided.Id, AppRequestStatus.Approved, "Yes.", "admin"));

        var html = await Html(await SignedIn(), "/admin");

        Assert.Contains("Pending requests", html, StringComparison.Ordinal);
        Assert.Contains("href=\"/admin/requests\"", html, StringComparison.Ordinal);
        Assert.Matches(@"<div class=""value"">1</div>\s*<div class=""label"">Pending requests</div>", html);
    }

    [Fact]
    public async Task The_decision_reaches_the_device_that_asked()
    {
        var request = _requests.Create("TESTPC", @"CONTOSO\jdoe", "Notepad++");
        await Decide(await SignedIn(), "Approve", request.Id, "Added to the catalog.", htmx: true);

        var device = _factory.CreateClient();
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _deviceToken);
        var mine = await device.GetFromJsonAsync<IReadOnlyList<AppRequest>>(ApiRoutes.Requests, Json);

        var answered = Assert.Single(mine!);
        Assert.Equal(AppRequestStatus.Approved, answered.Status);
        Assert.Equal("Added to the catalog.", answered.Reason);
        Assert.NotNull(answered.DecidedAt);
    }

    private CatalogStore SeedCatalog()
    {
        var catalog = new CatalogStore(_test.Database, "");
        catalog.Upsert(new CatalogEntry { Id = "Slack", Name = "Slack", Action1 = new Action1PackageRef { PackageId = "Slack_1" } });
        catalog.Upsert(new CatalogEntry { Id = "teams", Name = "Teams", Action1 = new Action1PackageRef { PackageId = "Teams_1" } });
        return catalog;
    }

    [Fact]
    public async Task Approving_with_an_app_id_links_the_request_in_the_same_write()
    {
        SeedCatalog();
        var request = _requests.Create("TESTPC", null, "Slack, please");
        var client = await SignedIn();

        var response = await Decide(client, "Approve", request.Id, "Added.", htmx: true, catalogAppId: " slack ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = _requests.Find(request.Id)!;
        Assert.Equal(AppRequestStatus.Approved, stored.Status);
        Assert.Equal("Slack", stored.CatalogAppId);
    }

    [Fact]
    public async Task Approving_with_an_unknown_app_id_decides_nothing()
    {
        var request = _requests.Create("TESTPC", null, "Slack");
        var client = await SignedIn();

        var response = await Decide(client, "Approve", request.Id, null, htmx: true, catalogAppId: "slack");

        Assert.Contains("No app with id 'slack' is in the catalog. Nothing was decided.", await Body(response), StringComparison.Ordinal);
        Assert.Equal(AppRequestStatus.Pending, _requests.Find(request.Id)!.Status);
    }

    [Fact]
    public async Task Approving_with_a_blank_app_id_approves_as_before()
    {
        var request = _requests.Create("TESTPC", null, "Slack");
        var client = await SignedIn();

        await Decide(client, "Approve", request.Id, null, htmx: true, catalogAppId: "  ");

        var stored = _requests.Find(request.Id)!;
        Assert.Equal(AppRequestStatus.Approved, stored.Status);
        Assert.Null(stored.CatalogAppId);
    }

    [Fact]
    public async Task Approve_and_add_records_the_approval_and_opens_the_create_form()
    {
        var request = _requests.Create("TESTPC", null, "Slack, for support");
        var client = await SignedIn();

        // The button has no hx-post, so it arrives as a plain form post even with htmx on the page.
        var response = await Decide(client, "ApproveAndAdd", request.Id, "Adding it now.", htmx: false);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/admin/catalog/new?fromRequest={request.Id}", response.Headers.Location?.OriginalString);
        var stored = _requests.Find(request.Id)!;
        Assert.Equal(AppRequestStatus.Approved, stored.Status);
        Assert.Equal("Adding it now.", stored.Reason);
        Assert.Null(stored.CatalogAppId);
    }

    [Fact]
    public async Task Approve_and_add_on_a_decided_request_shows_the_error_and_stays()
    {
        var request = _requests.Create("TESTPC", null, "Slack");
        _requests.Decide(request.Id, AppRequestStatus.Denied, "No.", "someone else");
        var client = await SignedIn();

        var response = await Decide(client, "ApproveAndAdd", request.Id, null, htmx: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("That request had already been decided.", await Body(response), StringComparison.Ordinal);
        Assert.Equal(AppRequestStatus.Denied, _requests.Find(request.Id)!.Status);
    }

    [Fact]
    public async Task An_approved_request_is_linked_relinked_and_unlinked_from_the_page()
    {
        SeedCatalog();
        var request = _requests.Create("TESTPC", null, "Chat");
        _requests.Decide(request.Id, AppRequestStatus.Approved, null, "admin");
        var client = await SignedIn();

        var linked = await Decide(client, "Link", request.Id, null, htmx: true, catalogAppId: "slack");
        Assert.Contains("Linked to Slack.", await Body(linked), StringComparison.Ordinal);
        Assert.Equal("Slack", _requests.Find(request.Id)!.CatalogAppId);

        var relinked = await Decide(client, "Link", request.Id, null, htmx: true, catalogAppId: "teams");
        Assert.Contains("Linked to Teams.", await Body(relinked), StringComparison.Ordinal);
        Assert.Equal("teams", _requests.Find(request.Id)!.CatalogAppId);

        var unknown = await Decide(client, "Link", request.Id, null, htmx: true, catalogAppId: "zoom");
        Assert.Contains("No app with id 'zoom' is in the catalog.", await Body(unknown), StringComparison.Ordinal);
        Assert.Equal("teams", _requests.Find(request.Id)!.CatalogAppId);

        var unlinked = await Decide(client, "Unlink", request.Id, null, htmx: true);
        Assert.Contains("The request no longer names a catalog app.", await Body(unlinked), StringComparison.Ordinal);
        Assert.Null(_requests.Find(request.Id)!.CatalogAppId);
    }

    [Fact]
    public async Task A_pending_or_missing_request_cannot_be_linked_from_the_page()
    {
        SeedCatalog();
        var request = _requests.Create("TESTPC", null, "Chat");
        var client = await SignedIn();

        var pending = await Decide(client, "Link", request.Id, null, htmx: true, catalogAppId: "Slack");
        Assert.Contains("Only an approved request can name a catalog app.", await Body(pending), StringComparison.Ordinal);

        var missing = await Decide(client, "Link", "nothing", null, htmx: true, catalogAppId: "Slack");
        Assert.Contains("No such request.", await Body(missing), StringComparison.Ordinal);
        Assert.Null(_requests.Find(request.Id)!.CatalogAppId);
    }

    [Fact]
    public async Task Rows_show_the_pending_choices_and_the_approved_link_in_each_state()
    {
        var catalog = SeedCatalog();
        var pending = _requests.Create("TESTPC", null, "Waiting");
        var unlinked = _requests.Create("TESTPC", null, "Unlinked");
        var visible = _requests.Create("TESTPC", null, "Visible");
        var hidden = _requests.Create("TESTPC", null, "Hidden");
        var deleted = _requests.Create("TESTPC", null, "Deleted");
        _requests.Decide(unlinked.Id, AppRequestStatus.Approved, null, "admin");
        _requests.Decide(visible.Id, AppRequestStatus.Approved, null, "admin", "Slack");
        _requests.Decide(hidden.Id, AppRequestStatus.Approved, null, "admin", "teams");
        catalog.Upsert(new CatalogEntry { Id = "zoom", Name = "Zoom", Action1 = new Action1PackageRef { PackageId = "Zoom_1" } });
        _requests.Decide(deleted.Id, AppRequestStatus.Approved, null, "admin", "zoom");
        Assert.True(catalog.SetHidden("teams", true));
        Assert.True(catalog.Delete("zoom"));
        var client = await SignedIn();

        var pendingHtml = await Html(client, "/admin/requests");
        Assert.Contains("Already in the catalog? Its id (optional, for Approve)", pendingHtml, StringComparison.Ordinal);
        Assert.Contains("Approve and add to the catalog", pendingHtml, StringComparison.Ordinal);
        Assert.Contains("formaction=\"/admin/requests?handler=ApproveAndAdd\">", pendingHtml, StringComparison.Ordinal);
        Assert.Contains("<datalist id=\"catalog-app-ids\">", pendingHtml, StringComparison.Ordinal);
        Assert.Contains("<option value=\"Slack\">Slack</option>", pendingHtml, StringComparison.Ordinal);

        var html = await Html(client, "/admin/requests?tab=approved");
        Assert.Contains("Catalog app: <a href=\"/admin/catalog/Slack\">Slack</a>", html, StringComparison.Ordinal);
        Assert.Contains("Catalog app: Teams (hidden, so devices are not offered it)", html, StringComparison.Ordinal);
        Assert.Contains("Catalog app: 'zoom', no longer in the catalog", html, StringComparison.Ordinal);

        // Only the unlinked row offers Add to the catalog; every approved row offers a way to change the link.
        Assert.Single(Regex.Matches(html, ">Add to the catalog</a>"));
        Assert.Contains($"href=\"/admin/catalog/new?fromRequest={unlinked.Id}\"", html, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Matches(html, "<summary>Catalog app</summary>").Count);
        Assert.Equal(3, Regex.Matches(html, ">Remove link</button>").Count);
    }

    [Fact]
    public async Task Request_text_is_encoded_in_the_row()
    {
        _requests.Create("TESTPC", null, "<script>alert(1)</script>");
        var client = await SignedIn();

        var raw = await client.GetStringAsync("/admin/requests");

        Assert.DoesNotContain("<script>alert(1)</script>", raw, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
