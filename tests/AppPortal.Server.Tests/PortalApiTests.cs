using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using AppPortal.Server.Devices;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AppPortal.Server.Tests;

public sealed class PortalApiTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _token;

    public PortalApiTests()
    {
        var catalogPath = Path.Combine(_test.Root, "catalog.json");
        File.WriteAllText(catalogPath, """
        {
          "apps": [
            { "id": "chrome", "name": "Google Chrome", "publisher": "Google", "description": "Browser", "category": "Browsers",
              "action1": { "packageId": "Google_Google_Chrome_1570243626751_builtin", "version": "latest" }, "match": { "nameContains": "Google Chrome" } },
            { "id": "ghost", "name": "Ghost App", "publisher": "Nobody", "description": "Not in the repository", "category": "Other",
              "action1": { "packageId": "missing_package", "version": "latest" } }
          ]
        }
        """);
        _token = new DeviceStore(_test.Database).Add("TESTPC", "endpoint-1234");

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:CatalogPath", catalogPath);
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
            builder.UseSetting("Portal:MaxActiveInstallsPerDevice", "1");
        });
    }

    private HttpClient Client(bool authenticated = true)
    {
        var client = _factory.CreateClient();
        if (authenticated)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        return client;
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        var response = await Client(authenticated: false).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_without_token_is_unauthorized()
    {
        var response = await Client(authenticated: false).GetAsync(ApiRoutes.Catalog);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Api_with_wrong_token_is_unauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "apd_not-a-real-token");
        var response = await client.GetAsync(ApiRoutes.Catalog);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Catalog_lists_apps_without_package_details()
    {
        var apps = await Client().GetFromJsonAsync<List<CatalogApp>>(ApiRoutes.Catalog, Json);
        Assert.NotNull(apps);
        Assert.Equal(2, apps.Count);
        Assert.Contains(apps, a => a.Id == "chrome" && a.Name == "Google Chrome");
        var raw = await Client().GetStringAsync(ApiRoutes.Catalog);
        Assert.DoesNotContain("packageId", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Device_reports_the_authenticated_device()
    {
        var device = await Client().GetFromJsonAsync<DeviceInfo>(ApiRoutes.Device, Json);
        Assert.NotNull(device);
        Assert.Equal("TESTPC", device.DeviceName);
        Assert.Equal("endpoint-1234", device.EndpointId);
        Assert.Equal("Connected", device.EndpointStatus);
    }

    [Fact]
    public async Task Install_runs_to_completion_and_shows_up_in_inventory()
    {
        var client = Client();
        var response = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chrome"), Json);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<InstallRequest>(Json);
        Assert.NotNull(created);
        Assert.Equal(InstallState.Queued, created.State);
        Assert.Equal("Google Chrome", created.AppName);

        InstallRequest? current = created;
        for (var i = 0; i < 5 && current!.State is InstallState.Queued or InstallState.Running; i++)
        {
            current = await client.GetFromJsonAsync<InstallRequest>($"{ApiRoutes.Installs}/{created.Id}", Json);
        }

        Assert.Equal(InstallState.Succeeded, current!.State);
        Assert.Equal(100, current.PercentComplete);
        Assert.NotNull(current.CompletedAt);

        var installed = await client.GetFromJsonAsync<List<InstalledApp>>(ApiRoutes.Installed, Json);
        Assert.NotNull(installed);
        Assert.Contains(installed, a => a.CatalogAppId == "chrome");

        var history = await client.GetFromJsonAsync<List<InstallRequest>>(ApiRoutes.Installs, Json);
        Assert.NotNull(history);
        Assert.Single(history, r => r.Id == created.Id);
    }

    [Fact]
    public async Task Duplicate_request_while_active_is_a_conflict_and_limit_is_enforced()
    {
        var client = Client();
        var first = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chrome"), Json);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);

        var duplicate = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chrome"), Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var other = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("ghost"), Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, other.StatusCode);
    }

    [Fact]
    public async Task Unknown_app_is_not_found_and_unresolvable_package_is_unprocessable()
    {
        var client = Client();
        var unknown = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("does-not-exist"), Json);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var ghost = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("ghost"), Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, ghost.StatusCode);
        var error = await ghost.Content.ReadFromJsonAsync<ErrorMessage>(Json);
        Assert.Contains("Software Repository", error!.Message);
    }

    [Fact]
    public async Task Install_records_are_scoped_to_the_requesting_device()
    {
        var client = Client();
        var created = await (await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chrome"), Json))
            .Content.ReadFromJsonAsync<InstallRequest>(Json);

        var otherToken = new DeviceStore(_test.Database).Add("OTHERPC", "endpoint-9999");
        var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherToken);
        var response = await other.GetAsync($"{ApiRoutes.Installs}/{created!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_account_the_client_sends_is_stored_against_the_install()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add(ApiHeaders.Requester, @"CONTOSO\jdoe");

        var created = await (await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chrome"), Json))
            .Content.ReadFromJsonAsync<InstallRequest>(Json);
        Assert.Equal(@"CONTOSO\jdoe", created!.RequestedBy);

        // And it survives the round trip through the database, not just the response the endpoint built.
        var listed = await client.GetFromJsonAsync<IReadOnlyList<InstallRequest>>(ApiRoutes.Installs, Json);
        Assert.Equal(@"CONTOSO\jdoe", Assert.Single(listed!).RequestedBy);
    }

    [Fact]
    public async Task A_client_that_sends_no_account_still_installs()
    {
        // A 0.2.x client knows nothing about the header. It must keep working, with no account recorded.
        var created = await (await Client().PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chrome"), Json))
            .Content.ReadFromJsonAsync<InstallRequest>(Json);

        Assert.Null(created!.RequestedBy);
    }

    [Fact]
    public async Task An_account_carrying_a_control_character_is_refused()
    {
        var client = Client();
        client.DefaultRequestHeaders.TryAddWithoutValidation(ApiHeaders.Requester, "CONTOSO\njdoe");

        var response = await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chrome"), Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_account_longer_than_the_column_allows_is_cut_to_length()
    {
        var client = Client();
        client.DefaultRequestHeaders.Add(ApiHeaders.Requester, new string('x', ApiHeaders.RequesterMaxLength + 40));

        var created = await (await client.PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest("chrome"), Json))
            .Content.ReadFromJsonAsync<InstallRequest>(Json);

        Assert.Equal(ApiHeaders.RequesterMaxLength, created!.RequestedBy!.Length);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
