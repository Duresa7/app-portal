using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
