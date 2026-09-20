using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using AppPortal.Server.Admin;
using AppPortal.Server.Devices;
using AppPortal.Server.Requests;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class AdminRequestsPageTests : IDisposable
{
    private const string Password = "a-long-enough-password";

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

        new AdminStore(_test.Database).Add("admin", Password);
        _deviceToken = new DeviceStore(_test.Database).Add("TESTPC", "endpoint-1234");
        _requests = new AppRequestStore(_test.Database);
    }

    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
    });

    /// <summary>
    /// Razor's encoder escapes more than the five XML characters: "Notepad++" reaches the page as
    /// "Notepad&#x2B;&#x2B;". Decoding once here lets every assertion below read like the screen does.
    /// </summary>
    private static async Task<string> Html(HttpClient client, string url)
        => WebUtility.HtmlDecode(await client.GetStringAsync(url));

    private static async Task<string> Body(HttpResponseMessage response)
        => WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    private static async Task<string> TokenFrom(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success, $"The form on {path} carried no antiforgery token.");
        return match.Groups[1].Value;
    }

    private async Task<HttpClient> SignedIn()
    {
        var client = Browser();
        var token = await TokenFrom(client, "/admin/login");
        var response = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "admin",
            ["Password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private async Task<HttpResponseMessage> Decide(
        HttpClient client, string handler, string id, string? reason, bool htmx)
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
        var response = await Browser().GetAsync("/admin/requests");

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

        var html = await Html(await SignedIn(), "/admin/requests");

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
    public async Task The_tabs_show_only_what_they_name()
    {
        var approved = _requests.Create("TESTPC", null, "Approved app");
        var denied = _requests.Create("TESTPC", null, "Denied app");
        _requests.Create("TESTPC", null, "Pending app");
        Assert.True(_requests.Decide(approved.Id, AppRequestStatus.Approved, "Yes.", "admin"));
        Assert.True(_requests.Decide(denied.Id, AppRequestStatus.Denied, "No.", "admin"));

        var client = await SignedIn();

        var pending = await Html(client, "/admin/requests?tab=pending");
        Assert.Contains("Pending app", pending, StringComparison.Ordinal);
        Assert.DoesNotContain("Approved app", pending, StringComparison.Ordinal);
        Assert.DoesNotContain("Denied app", pending, StringComparison.Ordinal);

        var approvedTab = await Html(client, "/admin/requests?tab=approved");
        Assert.Contains("Approved app", approvedTab, StringComparison.Ordinal);
        Assert.DoesNotContain("Pending app", approvedTab, StringComparison.Ordinal);

        var deniedTab = await Html(client, "/admin/requests?tab=denied");
        Assert.Contains("Denied app", deniedTab, StringComparison.Ordinal);
        Assert.DoesNotContain("Approved app", deniedTab, StringComparison.Ordinal);

        var all = await Html(client, "/admin/requests?tab=all");
        Assert.Contains("Pending app", all, StringComparison.Ordinal);
        Assert.Contains("Approved app", all, StringComparison.Ordinal);
        Assert.Contains("Denied app", all, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_tab_falls_back_to_pending_rather_than_showing_nothing()
    {
        _requests.Create("TESTPC", null, "Pending app");

        var html = await Html(await SignedIn(), "/admin/requests?tab=nonsense");

        Assert.Contains("Pending app", html, StringComparison.Ordinal);
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

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
