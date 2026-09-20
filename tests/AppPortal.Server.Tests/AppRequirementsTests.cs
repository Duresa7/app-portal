using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

using AppPortal.Server.Catalog;
using AppPortal.Server.Devices;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

/// <summary>
/// An app states what it needs and the portal repeats it. It never checks one and never refuses an
/// install over one: installing is not running, and predicting whether software will work later from
/// rules its vendor owns would block people from software that was fine.
/// </summary>
public sealed class AppRequirementsTests : IDisposable
{
    private const string Note = "Needs Secure Boot and TPM 2.0 turned on.";

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CatalogStore _catalog;
    private readonly string _token;

    public AppRequirementsTests()
    {
        _catalog = new CatalogStore(_test.Database, "");
        _token = new DeviceStore(_test.Database).Add("TESTPC", "endpoint-1234");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
    }

    [Fact]
    public void The_note_survives_a_save_and_a_read()
    {
        _catalog.Upsert(Entry(Note));

        Assert.Equal(Note, _catalog.Entries.Single().Requirements);
    }

    [Fact]
    public void Saving_an_app_again_does_not_quietly_drop_its_note()
    {
        // The engine override had exactly this bug before it was found, because both writers wrote a
        // constant into the column instead of the entry's value.
        _catalog.Upsert(Entry(Note));

        var renamed = Entry(Note);
        renamed.Name = "Renamed";
        _catalog.Upsert(renamed);

        var saved = _catalog.Entries.Single();
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal(Note, saved.Requirements);
    }

    [Fact]
    public void An_app_with_nothing_to_say_carries_nothing()
    {
        _catalog.Upsert(Entry(null));

        Assert.Null(_catalog.Entries.Single().Requirements);
    }

    [Fact]
    public async Task A_device_is_told_what_the_app_needs()
    {
        _catalog.Upsert(Entry(Note));

        var app = Assert.Single(await Catalog());

        Assert.Equal(Note, app.Requirements);
    }

    [Fact]
    public async Task An_app_with_requirements_is_still_offered_and_still_installs()
    {
        // The whole point. The portal says what it knows and the person decides, so a requirement is
        // never a reason the app is missing or the button is dead.
        _catalog.Upsert(Entry("Needs a vendor account, a working microphone and the moon in Gemini."));

        var app = Assert.Single(await Catalog());
        var response = await Client().PostAsJsonAsync(ApiRoutes.Installs, new CreateInstallRequest(app.Id));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public void A_note_longer_than_the_limit_is_still_stored_whole_by_the_store()
    {
        // The page is where the length is enforced. The store is not the place to silently truncate
        // somebody's words, and an import of an older catalog must not fail on one.
        var long_note = new string('x', CatalogLimits.MaxRequirementsLength + 50);

        _catalog.Upsert(Entry(long_note));

        Assert.Equal(long_note, _catalog.Entries.Single().Requirements);
    }

    private static CatalogEntry Entry(string? requirements) => new()
    {
        Id = "app",
        Name = "An App",
        Action1 = new Action1PackageRef { PackageId = "pkg", Version = "latest" },
        Requirements = requirements,
    };

    private async Task<IReadOnlyList<CatalogApp>> Catalog()
        => await Client().GetFromJsonAsync<IReadOnlyList<CatalogApp>>(ApiRoutes.Catalog) ?? [];

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
