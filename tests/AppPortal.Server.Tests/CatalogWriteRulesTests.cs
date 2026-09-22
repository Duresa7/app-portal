using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using AppPortal.Server.Action1;
using AppPortal.Server.Catalog;
using AppPortal.Server.Cli;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

/// <summary>
/// One catalog app, seven ways in: the store's two writers, the two admin API routes, the edit form,
/// the import on the catalog page, and <c>catalog import</c>. An app one of them accepts and another
/// refuses is an app an administrator can create in one place and then not save in the other, so
/// every case here goes through all seven and has to be answered the same way by each.
/// </summary>
public sealed class CatalogWriteRulesTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CatalogStore _catalog;

    public CatalogWriteRulesTests()
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
        _catalog = new CatalogStore(_test.Database, catalogPath);
    }

    /// <summary>
    /// An app wrong in one way, and the words and status every route has to answer it with. The
    /// requirements note is given as a length, and the prerequisites as a comma-separated list.
    /// </summary>
    public static TheoryData<string, int, string?, string, string, HttpStatusCode> Refused => new()
    {
        { "app", CatalogLimits.MaxRequirementsLength + 1, null, "",
            $"Requirements must be {CatalogLimits.MaxRequirementsLength} characters or fewer.", HttpStatusCode.BadRequest },
        { "app", 0, "both", "", "engineOverride must be action1, agent, or empty", HttpStatusCode.BadRequest },
        // GET /api/v1/admin/catalog/export is the export, so an app with that id could be written and
        // never read back by id.
        { "export", 0, null, "", "'export' is reserved", HttpStatusCode.BadRequest },
        { "Export", 0, null, "", "'Export' is reserved", HttpStatusCode.BadRequest },
        { "new", 0, null, "", "'new' is reserved", HttpStatusCode.BadRequest },
        { "app", 0, null, "absent", "'absent' is not in the catalog", HttpStatusCode.UnprocessableEntity },
        { "app", 0, null, "app", "loop", HttpStatusCode.UnprocessableEntity },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task Every_write_path_refuses_the_same_app_with_the_same_words(
        string id, int requirementsLength, string? engineOverride, string requires, string message, HttpStatusCode status)
    {
        var @case = new Case(id, requirementsLength == 0 ? null : new string('x', requirementsLength), engineOverride,
            requires.Split(',', StringSplitOptions.RemoveEmptyEntries), message, status);
        var admin = await Admin();
        var browser = await TestDatabase.SignedIn(_factory);

        // The store, both ways in.
        Assert.Contains(@case.Message, Refusal(() => _catalog.Upsert(Entry(@case))));
        Assert.Contains(@case.Message, Refusal(() => _catalog.Import([Entry(@case)])));

        // The admin API, both routes, with the same status for the same fault.
        var put = await admin.PutAsJsonAsync($"/api/v1/admin/catalog/{@case.Id}", Body(@case), Json);
        Assert.Equal(@case.Status, put.StatusCode);
        Assert.Contains(@case.Message, await MessageOf(put));

        var imported = await admin.PostAsync("/api/v1/admin/catalog/import",
            new StringContent(FileFor(@case), Encoding.UTF8, "application/json"));
        Assert.Equal(@case.Status, imported.StatusCode);
        Assert.Contains(@case.Message, await MessageOf(imported));

        // The edit form's create page.
        var form = await browser.PostAsync("/admin/catalog/new", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Id"] = @case.Id,
            ["Name"] = "An App",
            ["PackageId"] = "Vendor_App_builtin",
            ["Requirements"] = @case.Requirements ?? "",
            ["EngineOverride"] = @case.EngineOverride ?? "",
            ["Requires"] = string.Join('\n', @case.Requires),
            ["__RequestVerificationToken"] = await TestDatabase.TokenOn(browser, "/admin/catalog/new"),
        }));
        Assert.Equal(HttpStatusCode.OK, form.StatusCode);
        Assert.Contains(@case.Message, WebUtility.HtmlDecode(await form.Content.ReadAsStringAsync()));

        // The upload on the catalog page.
        var upload = new MultipartFormDataContent
        {
            { new StringContent(await TestDatabase.TokenOn(browser, "/admin/catalog")), "__RequestVerificationToken" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(FileFor(@case))), "file", "catalog.json" },
        };
        var page = await browser.PostAsync("/admin/catalog?handler=Import", upload);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains(@case.Message, WebUtility.HtmlDecode(await page.Content.ReadAsStringAsync()));

        // catalog import on the command line.
        var path = Path.Combine(_test.Root, "import.json");
        await File.WriteAllTextAsync(path, FileFor(@case));
        using var output = new StringWriter();
        Assert.Equal(1, await CatalogCli.RunAsync(["catalog", "import", path], _catalog, new FakeAction1Client(), output, CancellationToken.None));
        Assert.Contains(@case.Message, output.ToString());

        Assert.Empty(_catalog.Entries);
    }

    [Fact]
    public void The_limits_themselves_are_allowed()
    {
        var entry = new CatalogEntry
        {
            Id = "app",
            Name = "An App",
            Requirements = new string('x', CatalogLimits.MaxRequirementsLength),
            EngineOverride = "Agent",
            Action1 = new Action1PackageRef { PackageId = "Vendor_App_builtin" },
        };

        _catalog.Upsert(entry);

        var stored = _catalog.Find("app")!;
        Assert.Equal(CatalogLimits.MaxRequirementsLength, stored.Requirements!.Length);
        // Written the way the form's list names it, so the form opens with the choice still selected
        // rather than showing the server's default and clearing it on the next save.
        Assert.Equal(EngineLabel.Agent, stored.EngineOverride);
    }

    [Fact]
    public void A_blank_engine_override_in_a_file_follows_the_server()
    {
        _catalog.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "app", "name": "An App", "engineOverride": " ", "action1": { "packageId": "x" } } ] }
            """));

        Assert.Null(_catalog.Find("app")!.EngineOverride);
    }

    [Fact]
    public void An_import_may_require_an_app_later_in_the_same_file_or_already_in_the_catalog()
    {
        _catalog.Upsert(App("runtime"));

        // The file is checked as one set: 'game' names 'launcher' before the file reaches it, and
        // 'launcher' names an app the file never mentions because the catalog has it already.
        _catalog.Import(CatalogStore.Parse("""
            { "apps": [
              { "id": "game", "name": "A Game", "requires": ["launcher"], "action1": { "packageId": "g" } },
              { "id": "launcher", "name": "A Launcher", "requires": ["runtime"], "action1": { "packageId": "l" } }
            ] }
            """));

        Assert.Equal(["launcher"], _catalog.Find("game")!.Requires);
        Assert.Equal(["runtime"], _catalog.Find("launcher")!.Requires);
    }

    [Fact]
    public void A_prerequisite_named_in_another_case_is_stored_as_the_app_spells_its_id()
    {
        // Ids match in any case everywhere an administrator types one, but the table's foreign key
        // matches exactly. The check said yes, so the write has to manage it too.
        _catalog.Upsert(App("runtime"));

        _catalog.Upsert(App("game", "RUNTIME"));

        Assert.Equal(["runtime"], _catalog.Find("game")!.Requires);
    }

    [Fact]
    public void An_import_refuses_a_loop_it_closes_with_an_app_already_in_the_catalog()
    {
        _catalog.Upsert(App("launcher"));
        _catalog.Upsert(App("game", "launcher"));

        var failure = Assert.Throws<PrerequisiteException>(() => _catalog.Import(CatalogStore.Parse("""
            { "apps": [ { "id": "launcher", "name": "A Launcher", "requires": ["game"], "action1": { "packageId": "l" } } ] }
            """)));

        Assert.Contains("loop", failure.Message);
        Assert.Empty(_catalog.Find("launcher")!.Requires);
    }

    [Fact]
    public void An_import_that_turns_a_chain_round_is_judged_on_what_it_leaves_behind()
    {
        _catalog.Upsert(App("launcher"));
        _catalog.Upsert(App("game", "launcher"));

        // Stored, game needs launcher. The file reverses that, and because it names game with no
        // prerequisites, the old edge is gone once the file is in: there is no loop to refuse.
        _catalog.Import(CatalogStore.Parse("""
            { "apps": [
              { "id": "launcher", "name": "A Launcher", "requires": ["game"], "action1": { "packageId": "l" } },
              { "id": "game", "name": "A Game", "action1": { "packageId": "g" } }
            ] }
            """));

        Assert.Equal(["game"], _catalog.Find("launcher")!.Requires);
        Assert.Empty(_catalog.Find("game")!.Requires);
    }

    [Fact]
    public void An_import_with_one_bad_prerequisite_writes_none_of_the_file()
    {
        Assert.Throws<PrerequisiteException>(() => _catalog.Import(CatalogStore.Parse("""
            { "apps": [
              { "id": "fine", "name": "Fine", "action1": { "packageId": "f" } },
              { "id": "broken", "name": "Broken", "requires": ["absent"], "action1": { "packageId": "b" } }
            ] }
            """)));

        Assert.Empty(_catalog.Entries);
    }

    private sealed record Case(string Id, string? Requirements, string? EngineOverride, string[] Requires, string Message, HttpStatusCode Status);

    private static CatalogEntry App(string id, params string[] requires) => new()
    {
        Id = id,
        Name = id,
        Requires = [.. requires],
        Action1 = new Action1PackageRef { PackageId = id + "_builtin" },
    };

    private static CatalogEntry Entry(Case @case) => new()
    {
        Id = @case.Id,
        Name = "An App",
        Requirements = @case.Requirements,
        EngineOverride = @case.EngineOverride,
        Requires = [.. @case.Requires],
        Action1 = new Action1PackageRef { PackageId = "Vendor_App_builtin" },
    };

    private static AdminCatalogApp Body(Case @case) => new(
        @case.Id, "An App", EngineOverride: @case.EngineOverride, Requirements: @case.Requirements,
        Requires: @case.Requires, Action1: new AdminAction1Package("Vendor_App_builtin"));

    private static string FileFor(Case @case) => JsonSerializer.Serialize(new
    {
        apps = new[]
        {
            new
            {
                id = @case.Id,
                name = "An App",
                requirements = @case.Requirements,
                engineOverride = @case.EngineOverride,
                requires = @case.Requires,
                action1 = new { packageId = "Vendor_App_builtin", version = "latest" },
            },
        },
    }, Json);

    private static string Refusal(Action write)
    {
        var failure = Assert.ThrowsAny<Exception>(write);
        Assert.True(failure is InvalidDataException or PrerequisiteException, $"Refused with {failure.GetType().Name}: {failure.Message}");
        return failure.Message;
    }

    private static async Task<string> MessageOf(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<ErrorMessage>(Json))!.Message;

    private async Task<HttpClient> Admin()
    {
        var client = _factory.CreateClient();
        var issued = await client.PostAsJsonAsync("/api/v1/admin/session",
            new { username = TestDatabase.AdminUsername, password = TestDatabase.AdminPassword }, Json);
        Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
        var body = await issued.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("token").GetString());
        return client;
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
