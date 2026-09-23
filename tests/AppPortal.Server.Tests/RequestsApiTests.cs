using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Server.Requests;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class RequestsApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _token;

    public RequestsApiTests()
    {
        var catalogPath = Path.Combine(_test.Root, "catalog.json");
        File.WriteAllText(catalogPath, """{ "apps": [] }""");
        _token = new DeviceStore(_test.Database).Add("TESTPC", "endpoint-1234");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:CatalogPath", catalogPath);
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
    }

    private HttpClient Client(string? token = null, string? user = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? _token);
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(ApiHeaders.Requester, user);
        }

        return client;
    }

    [Fact]
    public async Task A_request_is_created_with_the_device_and_the_account()
    {
        var response = await Client(user: @"CONTOSO\jdoe")
            .PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest("  Notepad++, for config files  "), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // A device reads its requests as one list and there is no route for one request, so a
        // Location header would name a path that answers 404.
        Assert.Null(response.Headers.Location);

        var created = await response.Content.ReadFromJsonAsync<AppRequest>(Json);
        Assert.Equal("Notepad++, for config files", created!.Text);
        Assert.Equal("TESTPC", created.DeviceName);
        Assert.Equal(@"CONTOSO\jdoe", created.RequestedBy);
        Assert.Equal(AppRequestStatus.Pending, created.Status);
        Assert.Null(created.Reason);
        Assert.Null(created.DecidedAt);
    }

    [Fact]
    public async Task Empty_text_is_refused()
    {
        var response = await Client().PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest("   "), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Text_past_the_limit_is_refused()
    {
        var tooLong = new string('x', AppRequestLimits.MaxTextLength + 1);

        var response = await Client().PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest(tooLong), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Text_exactly_at_the_limit_is_accepted()
    {
        var atLimit = new string('x', AppRequestLimits.MaxTextLength);

        var response = await Client().PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest(atLimit), Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_device_with_too_many_pending_requests_must_wait()
    {
        var client = Client();
        for (var i = 0; i < AppRequestLimits.MaxPendingPerDevice; i++)
        {
            var ok = await client.PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest($"request {i}"), Json);
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var refused = await client.PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest("one too many"), Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // A decision frees the allowance again: the cap is on undecided requests, not on lifetime ones.
        var store = new AppRequestStore(_test.Database);
        var pending = store.ListForDevice("TESTPC").First();
        Assert.True(store.Decide(pending.Id, AppRequestStatus.Approved, "Fine.", "admin"));

        var afterDecision = await client.PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest("room again"), Json);
        Assert.Equal(HttpStatusCode.Created, afterDecision.StatusCode);
    }

    [Fact]
    public async Task A_device_sees_only_its_own_requests_newest_first()
    {
        await Client().PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest("first"), Json);
        await Client().PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest("second"), Json);

        var otherToken = new DeviceStore(_test.Database).Add("OTHERPC", "endpoint-9999");
        await Client(otherToken).PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest("not yours"), Json);

        var mine = await Client().GetFromJsonAsync<IReadOnlyList<AppRequest>>(ApiRoutes.Requests, Json);

        Assert.Equal(2, mine!.Count);
        Assert.DoesNotContain(mine, r => r.Text == "not yours");
        Assert.True(mine[0].CreatedAt >= mine[1].CreatedAt, "Requests are not newest first.");
    }

    [Fact]
    public async Task Requests_need_a_device_token()
    {
        var anonymous = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(ApiRoutes.Requests)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest("hello"), Json)).StatusCode);
    }

    [Fact]
    public async Task A_client_that_sends_no_account_still_files_a_request()
    {
        var created = await (await Client().PostAsJsonAsync(ApiRoutes.Requests, new CreateAppRequest("anonymous ask"), Json))
            .Content.ReadFromJsonAsync<AppRequest>(Json);

        Assert.Null(created!.RequestedBy);
    }

    [Fact]
    public void A_decision_records_who_made_it_and_when()
    {
        var store = new AppRequestStore(_test.Database);
        var created = store.Create("TESTPC", @"CONTOSO\jdoe", "Slack");

        Assert.True(store.Decide(created.Id, AppRequestStatus.Denied, "Use Teams.", "admin"));

        var decided = store.Find(created.Id)!;
        Assert.Equal(AppRequestStatus.Denied, decided.Status);
        Assert.Equal("Use Teams.", decided.Reason);
        Assert.Equal("admin", decided.DecidedBy);
        Assert.NotNull(decided.DecidedAt);
    }

    private CatalogStore Catalog()
    {
        var catalog = new CatalogStore(_test.Database, "");
        catalog.Upsert(new CatalogEntry { Id = "Slack-App", Name = "Slack", Action1 = new Action1PackageRef { PackageId = "Slack_1" } });
        return catalog;
    }

    [Fact]
    public void An_approval_can_name_the_app_in_the_same_write()
    {
        Catalog();
        var store = new AppRequestStore(_test.Database);
        var created = store.Create("TESTPC", null, "Slack");

        Assert.True(store.Decide(created.Id, AppRequestStatus.Approved, "Fine.", "admin", "Slack-App"));

        var decided = store.Find(created.Id)!;
        Assert.Equal(AppRequestStatus.Approved, decided.Status);
        Assert.Equal("Slack-App", decided.CatalogAppId);
        Assert.Equal("Slack", decided.CatalogAppName);
        Assert.False(decided.CatalogAppHidden);
    }

    [Fact]
    public void A_denial_cannot_name_an_app()
    {
        var store = new AppRequestStore(_test.Database);
        var created = store.Create("TESTPC", null, "Slack");

        Assert.Throws<ArgumentException>(() => store.Decide(created.Id, AppRequestStatus.Denied, null, "admin", "Slack-App"));
        Assert.Equal(AppRequestStatus.Pending, store.Find(created.Id)!.Status);
    }

    [Fact]
    public void Link_says_why_it_could_not_link()
    {
        Catalog();
        var store = new AppRequestStore(_test.Database);
        var pending = store.Create("TESTPC", null, "pending");
        var denied = store.Create("TESTPC", null, "denied");
        var approved = store.Create("TESTPC", null, "approved");
        store.Decide(denied.Id, AppRequestStatus.Denied, null, "admin");
        store.Decide(approved.Id, AppRequestStatus.Approved, null, "admin");

        Assert.Equal(RequestLinkResult.NoSuchRequest, store.Link("missing", "Slack-App"));
        Assert.Equal(RequestLinkResult.NotApproved, store.Link(pending.Id, "Slack-App"));
        Assert.Equal(RequestLinkResult.NotApproved, store.Link(denied.Id, "Slack-App"));
        Assert.Equal(RequestLinkResult.NoSuchApp, store.Link(approved.Id, "teams"));

        // A request that does not exist is reported before an app that does not exist either.
        Assert.Equal(RequestLinkResult.NoSuchRequest, store.Link("missing", "teams"));
        Assert.Null(store.Find(pending.Id)!.CatalogAppId);
        Assert.Null(store.Find(denied.Id)!.CatalogAppId);
        Assert.Null(store.Find(approved.Id)!.CatalogAppId);
    }

    [Fact]
    public void Link_stores_the_catalog_spelling_and_can_be_changed_and_cleared()
    {
        var catalog = Catalog();
        catalog.Upsert(new CatalogEntry { Id = "teams", Name = "Teams", Action1 = new Action1PackageRef { PackageId = "Teams_1" } });
        var store = new AppRequestStore(_test.Database);
        var approved = store.Create("TESTPC", null, "chat");
        store.Decide(approved.Id, AppRequestStatus.Approved, null, "admin");

        Assert.Equal(RequestLinkResult.Linked, store.Link(approved.Id, " slack-app "));
        Assert.Equal("Slack-App", store.Find(approved.Id)!.CatalogAppId);
        Assert.Equal("Slack", store.Find(approved.Id)!.CatalogAppName);

        Assert.Equal(RequestLinkResult.Linked, store.Link(approved.Id, "TEAMS"));
        Assert.Equal("teams", store.Find(approved.Id)!.CatalogAppId);

        Assert.Equal(RequestLinkResult.Linked, store.Link(approved.Id, "  "));
        var cleared = store.Find(approved.Id)!;
        Assert.Null(cleared.CatalogAppId);
        Assert.Null(cleared.CatalogAppName);
        Assert.Equal(AppRequestStatus.Approved, cleared.Status);
    }

    [Fact]
    public async Task The_device_sees_the_link_only_while_the_app_is_offered()
    {
        var catalog = Catalog();
        var store = new AppRequestStore(_test.Database);
        var created = store.Create("TESTPC", null, "Slack, please");
        store.Decide(created.Id, AppRequestStatus.Approved, "Fine.", "admin", "Slack-App");

        async Task<AppRequest> Mine()
            => Assert.Single((await Client().GetFromJsonAsync<IReadOnlyList<AppRequest>>(ApiRoutes.Requests, Json))!);

        var linked = await Mine();
        Assert.Equal("Slack-App", linked.CatalogAppId);
        Assert.Equal("Slack", linked.CatalogAppName);

        Assert.True(catalog.SetHidden("Slack-App", true));
        var hidden = await Mine();
        Assert.Null(hidden.CatalogAppId);
        Assert.Null(hidden.CatalogAppName);
        Assert.Equal(AppRequestStatus.Approved, hidden.Status);
        Assert.Equal("Fine.", hidden.Reason);
        Assert.True(store.Find(created.Id)!.CatalogAppHidden);

        Assert.True(catalog.SetHidden("Slack-App", false));
        Assert.Equal("Slack-App", (await Mine()).CatalogAppId);

        // Deleting the app is never refused because a request names it; the request stays approved
        // and the device sees a plain approval again.
        Assert.True(catalog.Delete("Slack-App"));
        var gone = await Mine();
        Assert.Null(gone.CatalogAppId);
        Assert.Null(gone.CatalogAppName);
        Assert.Equal(AppRequestStatus.Approved, gone.Status);
        Assert.Equal("Fine.", gone.Reason);

        var record = store.Find(created.Id)!;
        Assert.Equal("Slack-App", record.CatalogAppId);
        Assert.Null(record.CatalogAppName);
    }

    [Fact]
    public void Json_without_the_link_fields_reads_as_no_link_and_older_shapes_ignore_them()
    {
        var old = """
            {"id":"r1","text":"Slack","deviceName":"PC","requestedBy":null,"status":"Approved","reason":null,
             "createdAt":"2026-01-01T00:00:00+00:00","decidedAt":"2026-01-02T00:00:00+00:00"}
            """;
        var parsed = JsonSerializer.Deserialize<AppRequest>(old, Json)!;
        Assert.Null(parsed.CatalogAppId);
        Assert.Null(parsed.CatalogAppName);

        var admin = JsonSerializer.Deserialize<AdminRequest>(old, Json)!;
        Assert.Null(admin.CatalogAppId);
        Assert.Null(admin.CatalogAppName);
        Assert.False(admin.CatalogAppHidden);

        var linked = JsonSerializer.Serialize(new AdminRequest(
            "r1", "Slack", "PC", null, AppRequestStatus.Approved, null, "admin",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "slack", "Slack", true), Json);
        var older = JsonSerializer.Deserialize<OlderAdminRequest>(linked, Json)!;
        Assert.Equal("r1", older.Id);
        Assert.Equal(AppRequestStatus.Approved, older.Status);
    }

    /// <summary>AdminRequest as it was before M6-02, which an older client still deserializes into.</summary>
    private sealed record OlderAdminRequest(
        string Id, string Text, string DeviceName, string? RequestedBy, AppRequestStatus Status,
        string? Reason, string? DecidedBy, DateTimeOffset CreatedAt, DateTimeOffset? DecidedAt);

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
