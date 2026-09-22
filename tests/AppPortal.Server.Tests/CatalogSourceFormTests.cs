using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using AppPortal.Server.Catalog;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

/// <summary>
/// The catalog edit page's one Source selector. An administrator says where an app comes from and
/// names it there; every shape the catalog can hold has to be reachable that way, and an app saved
/// before the selector existed has to come back out of the form exactly as it went in.
/// </summary>
public sealed partial class CatalogSourceFormTests : IDisposable
{
    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CatalogStore _catalog;

    public CatalogSourceFormTests()
    {
        _catalog = new CatalogStore(_test.Database, "");
        var catalogPath = Path.Combine(_test.Root, "catalog.json");
        File.WriteAllText(catalogPath, """{ "apps": [] }""");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Portal:CatalogPath", catalogPath);
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });
        _test.AddAdmin();
    }

    public static TheoryData<string, string, string> Sources => new()
    {
        { "action1", "", "machine" },
        { "winget", "Valve.Steam", "user" },
        { "msstore", "9WZDNCRFJ3TJ", "user" },
        { "choco", "notepadplusplus", "machine" },
        { "scoop", "extras/vscode", "user" },
        { "npm", "@angular/cli", "machine" },
        { "yarn", "typescript", "user" },
        { "bun", "cowsay", "user" },
        { "pip", "requests", "machine" },
        { "cargo", "ripgrep", "user" },
        { "vcpkg", "curl[ssl]", "machine" },
        { "dotnet-tool", "dotnetsay", "user" },
        { "powershell-module", "Pester", "machine" },
        { "powershell5-module", "PSReadLine", "user" },
        { "direct", "", "machine" },
    };

    [Theory]
    [MemberData(nameof(Sources))]
    public async Task Every_source_is_reachable_from_the_selector_and_reads_back_as_saved(string source, string id, string scope)
    {
        var admin = await TestDatabase.SignedIn(_factory);
        var form = new Dictionary<string, string>
        {
            ["Id"] = "app",
            ["Name"] = "An App",
            ["Source"] = source,
            ["SourceId"] = id,
            ["SourceScope"] = scope,
            ["PackageId"] = source == "action1" ? "Vendor_App_builtin" : "",
            ["DirectUrl"] = PackageDefinitionTests.Direct.Url,
            ["DirectSha256"] = PackageDefinitionTests.Direct.Sha256,
            ["DirectInstallerType"] = "exe",
            ["DirectSizeBytes"] = "1000",
            ["__RequestVerificationToken"] = await TestDatabase.TokenOn(admin, "/admin/catalog/new"),
        };

        var response = await admin.PostAsync("/admin/catalog/new", new FormUrlEncodedContent(form));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = Assert.Single(_catalog.Entries);
        switch (source)
        {
            case "action1":
                Assert.Null(saved.Agent);
                Assert.Equal("Vendor_App_builtin", saved.Action1.PackageId);
                break;
            case "winget" or "msstore":
                var winget = Assert.IsType<WingetPackageDefinition>(saved.Agent);
                Assert.Equal((source, id, scope), (winget.Source, winget.Id, winget.Scope));
                break;
            case "direct":
                var direct = Assert.IsType<DirectPackageDefinition>(saved.Agent);
                Assert.Equal(PackageDefinitionTests.Direct.Url, direct.Url);
                break;
            default:
                var managed = Assert.IsType<ManagedPackageDefinition>(saved.Agent);
                Assert.Equal((source, id, scope), (managed.Manager, managed.Id, managed.Scope));
                break;
        }

        // Reopening the app shows the same source chosen.
        var html = await admin.GetStringAsync("/admin/catalog/app");
        Assert.Equal(source, Fields(html)["Source"]);
    }

    [Fact]
    public async Task Apps_saved_before_the_selector_come_back_out_of_the_form_unchanged()
    {
        _catalog.Import(CatalogStore.Parse("""
            { "apps": [
              { "id": "chrome", "name": "Google Chrome", "publisher": "Google", "category": "Browsers", "featured": true,
                "action1": { "packageId": "Google_Chrome_builtin", "version": "latest" },
                "match": { "nameEquals": "Google Chrome" } },
              { "id": "steam", "name": "Steam", "hidden": true, "engineOverride": "agent", "userRemovable": true,
                "requirements": "Needs a Steam account.",
                "action1": { "packageId": "Valve_Steam_builtin", "version": "2.10" },
                "agent": { "kind": "winget", "id": "Valve.Steam", "scope": "user", "version": "2.10.91.91",
                           "extraArgs": "--locale en-US", "requiresReboot": true } },
              { "id": "whiteboard", "name": "Microsoft Whiteboard",
                "agent": { "kind": "winget", "id": "9MSPC6MP8FM4", "scope": "user", "source": "msstore" } },
              { "id": "valorant", "name": "Valorant", "requires": ["steam"],
                "agent": { "kind": "direct", "url": "https://vendor.example/valorant.exe", "sha256": "SHA",
                           "installerType": "exe", "silentArgs": "", "sizeBytes": 123456, "uninstallKey": "Riot Game valorant.live",
                           "scope": "machine", "requiresReboot": false } },
              { "id": "vscode", "name": "Visual Studio Code",
                "agent": { "kind": "managed", "manager": "scoop", "id": "extras/vscode", "scope": "user" } },
              { "id": "ripgrep", "name": "ripgrep",
                "agent": { "kind": "managed", "manager": "cargo", "id": "ripgrep", "scope": "user", "version": "14.1.0" } }
            ] }
            """.Replace("\"SHA\"", "\"" + PackageDefinitionTests.Direct.Sha256 + "\"", StringComparison.Ordinal)));
        var before = _catalog.Entries.ToDictionary(e => e.Id, Serialize);
        var admin = await TestDatabase.SignedIn(_factory);

        foreach (var id in before.Keys)
        {
            // What a browser would send having changed nothing: every field as the page drew it.
            var fields = Fields(await admin.GetStringAsync("/admin/catalog/" + id));
            var response = await admin.PostAsync("/admin/catalog/" + id, new FormUrlEncodedContent(fields));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        var after = _catalog.Entries.ToDictionary(e => e.Id, Serialize);
        Assert.All(before, pair => Assert.Equal(pair.Value, after[pair.Key]));
    }

    [Fact]
    public async Task An_app_with_both_packages_can_still_choose_its_engine()
    {
        var admin = await TestDatabase.SignedIn(_factory);
        var response = await admin.PostAsync("/admin/catalog/new", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Id"] = "steam",
            ["Name"] = "Steam",
            ["Source"] = "winget",
            ["SourceId"] = "Valve.Steam",
            ["SourceScope"] = "machine",
            ["PackageId"] = "Valve_Steam_builtin",
            ["EngineOverride"] = "agent",
            ["__RequestVerificationToken"] = await TestDatabase.TokenOn(admin, "/admin/catalog/new"),
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = Assert.Single(_catalog.Entries);
        Assert.True(saved.HasAction1);
        Assert.IsType<WingetPackageDefinition>(saved.Agent);
        Assert.Equal("agent", saved.EngineOverride);
    }

    [Fact]
    public async Task The_sentence_under_the_form_follows_the_fields_without_saving()
    {
        var admin = await TestDatabase.SignedIn(_factory);
        var token = await TestDatabase.TokenOn(admin, "/admin/catalog/new");
        var request = new HttpRequestMessage(HttpMethod.Post, "/admin/catalog/new?handler=Describe")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Id"] = "chrome",
                ["Name"] = "Google <Chrome>",
                ["Source"] = "winget",
                ["SourceId"] = "Google.Chrome",
                ["SourceScope"] = "machine",
            }),
        };
        request.Headers.Add("RequestVerificationToken", token);

        var html = await (await admin.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Equal("Installs Google &lt;Chrome&gt; for everyone on the PC, through winget.", html);
        Assert.Empty(_catalog.Entries);
    }

    [Fact]
    public async Task The_catalog_list_names_the_agents_source_for_an_agent_only_app()
    {
        _catalog.Upsert(new CatalogEntry
        {
            Id = "vscode",
            Name = "Visual Studio Code",
            Agent = new ManagedPackageDefinition("scoop", "extras/vscode", "user"),
        });
        var admin = await TestDatabase.SignedIn(_factory);

        var html = await admin.GetStringAsync("/admin/catalog");

        Assert.Contains("<td class=\"muted\">Scoop</td>", html);
    }

    [Fact]
    public void A_persons_card_for_a_scoop_app_and_a_winget_app_differ_only_where_the_catalog_does()
    {
        // Which tool installs it is the administrator's business. The person sees a name, a size when
        // one is known, and who it installs for.
        static CatalogEntry App(PackageDefinition agent) => new()
        {
            Id = "editor",
            Name = "An Editor",
            Publisher = "Vendor",
            Category = "Developer tools",
            Agent = agent,
        };

        var winget = App(new WingetPackageDefinition("Vendor.Editor", "user")).ToPublic("agent");
        var scoop = App(new ManagedPackageDefinition("scoop", "extras/editor", "user")).ToPublic("agent");
        var store = App(new WingetPackageDefinition("9WZDNCRFJ3TJ", "user", Source: WingetSources.Store)).ToPublic("agent");

        Assert.Equal(JsonSerializer.Serialize(winget), JsonSerializer.Serialize(scoop));
        Assert.Equal(JsonSerializer.Serialize(winget), JsonSerializer.Serialize(store));
    }

    private static string Serialize(CatalogEntry entry) => JsonSerializer.Serialize(entry, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    /// <summary>Every field the form would post, read the way a browser reads it.</summary>
    private static Dictionary<string, string> Fields(string html)
    {
        var form = html[html.IndexOf("<form method=\"post\" id=\"catalog-form\"", StringComparison.Ordinal)..];
        form = form[..(form.IndexOf("</form>", StringComparison.Ordinal))];
        var fields = new Dictionary<string, string>();
        foreach (Match input in InputTag().Matches(form))
        {
            var attributes = Attributes(input.Value);
            if (!attributes.TryGetValue("name", out var name)
                || attributes.GetValueOrDefault("type") is "submit" or "button")
            {
                continue;
            }

            if (attributes.GetValueOrDefault("type") == "checkbox")
            {
                if (attributes.ContainsKey("checked"))
                {
                    fields[name] = attributes.GetValueOrDefault("value", "on");
                }

                continue;
            }

            fields[name] = attributes.GetValueOrDefault("value", "");
        }

        foreach (Match area in TextArea().Matches(form))
        {
            fields[Attributes(area.Groups["tag"].Value)["name"]] = WebUtility.HtmlDecode(area.Groups["body"].Value);
        }

        foreach (Match select in Select().Matches(form))
        {
            var options = OptionTag().Matches(select.Groups["body"].Value).Select(o => Attributes(o.Value)).ToList();
            var chosen = options.FirstOrDefault(o => o.ContainsKey("selected")) ?? options.First();
            fields[Attributes(select.Groups["tag"].Value)["name"]] = chosen.GetValueOrDefault("value", "");
        }

        return fields;
    }

    private static Dictionary<string, string> Attributes(string tag) => AttributePair().Matches(tag)
        .ToDictionary(m => m.Groups["key"].Value, m => WebUtility.HtmlDecode(m.Groups["value"].Value));

    [GeneratedRegex("<input\\b[^>]*>")]
    private static partial Regex InputTag();

    [GeneratedRegex("(?<tag><textarea\\b[^>]*>)(?<body>.*?)</textarea>", RegexOptions.Singleline)]
    private static partial Regex TextArea();

    [GeneratedRegex("(?<tag><select\\b[^>]*>)(?<body>.*?)</select>", RegexOptions.Singleline)]
    private static partial Regex Select();

    [GeneratedRegex("<option\\b[^>]*>")]
    private static partial Regex OptionTag();

    [GeneratedRegex("(?<key>[a-zA-Z_][\\w-]*)=\"(?<value>[^\"]*)\"")]
    private static partial Regex AttributePair();

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
