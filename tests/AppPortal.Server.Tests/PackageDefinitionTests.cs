using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using AppPortal.Server.Action1;
using AppPortal.Server.Catalog;
using AppPortal.Server.Cli;
using AppPortal.Shared;

namespace AppPortal.Server.Tests;

public sealed class PackageDefinitionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static DirectPackageDefinition Direct => new("https://vendor.example/installer.exe", new string('a', 64),
        "exe", "/S", 5_000_000_000L, "Vendor Application");

    [Fact]
    public void Direct_json_preserves_the_frozen_shape_and_a_multi_gigabyte_size()
    {
        PackageDefinition definition = Direct;
        definition.Validate();
        var json = JsonSerializer.Serialize(definition, Json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(["kind", "url", "sha256", "installerType", "silentArgs", "sizeBytes", "uninstallKey", "scope", "requiresReboot"],
            document.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("machine", document.RootElement.GetProperty("scope").GetString());
        Assert.False(document.RootElement.GetProperty("requiresReboot").GetBoolean());
        Assert.Equal("direct", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal(5_000_000_000L, document.RootElement.GetProperty("sizeBytes").GetInt64());
        Assert.Equal(definition, JsonSerializer.Deserialize<PackageDefinition>(json, Json));
    }

    [Fact]
    public void Winget_json_preserves_optional_fields()
    {
        const string json = """{"kind":"winget","id":"Valve.Steam","scope":"machine","version":null,"extraArgs":null,"requiresReboot":false}""";
        var definition = JsonSerializer.Deserialize<PackageDefinition>(json, Json)!;
        definition.Validate();
        Assert.Equal(json, JsonSerializer.Serialize(definition, Json));
        var pinned = new WingetPackageDefinition("Vendor.App", "user", "1.2", "--silent", RequiresReboot: true);
        Assert.Equal(pinned, JsonSerializer.Deserialize<PackageDefinition>(JsonSerializer.Serialize<PackageDefinition>(pinned, Json), Json));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public void Unknown_kinds_are_rejected(string kind)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<PackageDefinition>($$"""{"kind":"{{kind}}"}""", Json));
    }

    [Fact]
    public void Catalog_parse_rejects_a_missing_discriminator_and_missing_direct_fields()
    {
        Assert.Throws<InvalidDataException>(() => CatalogStore.Parse("""
            {"apps":[{"id":"bad","name":"Bad","agent":{"id":"Valve.Steam"}}]}
            """));
        Assert.Throws<InvalidDataException>(() => CatalogStore.Parse("""
            {"apps":[{"id":"bad","name":"Bad","agent":{"kind":"direct","url":"https://vendor.example/app.exe"}}]}
            """));
    }

    [Fact]
    public void Direct_validation_rejects_missing_or_invalid_fields()
    {
        DirectPackageDefinition[] invalid =
        [
            Direct with { Url = "file:///tmp/installer.exe" },
            Direct with { Url = "relative.exe" },
            Direct with { Url = "https://user:password@vendor.example/app.exe" },
            Direct with { Sha256 = null! },
            Direct with { Sha256 = "" },
            Direct with { Sha256 = new string('a', 63) },
            Direct with { Sha256 = new string('g', 64) },
            Direct with { InstallerType = "zip" },
            Direct with { InstallerType = "msi", SilentArgs = " " },
            Direct with { SizeBytes = 0 },
            Direct with { SizeBytes = -1 },
            Direct with { Scope = "everyone" },
            Direct with { Scope = "" },
        ];
        foreach (var definition in invalid)
        {
            Assert.Throws<InvalidDataException>(definition.Validate);
        }
    }

    [Theory]
    [InlineData("msi", "/qn")]
    [InlineData("exe", "/quiet")]
    [InlineData("msix", "--silent")]
    public void Direct_validation_accepts_vendor_arguments_and_uppercase_hashes(string installerType, string silentArgs)
    {
        (Direct with { InstallerType = installerType, SilentArgs = silentArgs, Sha256 = new string('A', 64) }).Validate();
    }

    [Fact]
    public void An_msix_needs_no_arguments_and_no_uninstall_key()
    {
        // The packaging API installs one by name. There is no command line to be silent on, and the
        // identity is a package family name rather than an entry under the Uninstall key.
        (Direct with { InstallerType = "msix", SilentArgs = "", UninstallKey = null }).Validate();
        Assert.Throws<InvalidDataException>((Direct with { InstallerType = "msi", SilentArgs = "" }).Validate);
    }

    [Fact]
    public void An_exe_that_is_silent_on_its_own_needs_no_arguments()
    {
        // Some installers are silent by default and publish no switch. winget runs those with no
        // arguments. Demanding a switch here left an administrator to invent one.
        (Direct with { InstallerType = "exe", SilentArgs = "" }).Validate();
        (Direct with { InstallerType = "exe", SilentArgs = " " }).Validate();
    }

    [Fact]
    public void An_unknown_installer_type_says_what_to_use_instead()
    {
        var error = Assert.Throws<InvalidDataException>((Direct with { InstallerType = "nsis" }).Validate);
        Assert.Contains("exe", error.Message);
        Assert.Contains("Nullsoft", error.Message);
    }

    [Fact]
    public void A_definition_carries_its_scope_and_its_restart_through_json()
    {
        // The executor reads both off the base record without caring which kind it has in hand.
        PackageDefinition[] perUser =
        [
            new WingetPackageDefinition("Vendor.App", "user", RequiresReboot: true),
            Direct with { Scope = "user", RequiresReboot = true },
        ];
        foreach (var definition in perUser)
        {
            definition.Validate();
            var round = JsonSerializer.Deserialize<PackageDefinition>(JsonSerializer.Serialize(definition, Json), Json)!;
            Assert.Equal("user", round.Scope);
            Assert.True(round.RequiresReboot);
            Assert.Equal(definition, round);
        }
    }

    [Theory]
    [InlineData("", "machine")]
    [InlineData("Steam", "machine")]
    [InlineData("Vendor/../App", "machine")]
    [InlineData("Valve.Steam", "everywhere")]
    public void Winget_validation_rejects_invalid_ids_and_scope(string id, string scope)
    {
        Assert.Throws<InvalidDataException>(() => new WingetPackageDefinition(id, scope).Validate());
    }

    [Fact]
    public async Task Fetch_hashes_the_stream_and_counts_bytes_without_buffering_the_response()
    {
        var data = Encoding.UTF8.GetBytes("a vendor installer");
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamOnlyContent(data),
        }));
        var result = await new PackageHelpers(client).FetchAndHashAsync(Direct.Url, data.Length, CancellationToken.None);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), result.Sha256);
        Assert.Equal(data.LongLength, result.SizeBytes);
        Assert.Equal(2_147_483_648L, PackageHelpers.DefaultMaxDownloadBytes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fetch_rejects_oversized_downloads_with_or_without_a_content_length(bool knownLength)
    {
        using var client = new HttpClient(new StubHandler(_ =>
        {
            var content = new StreamOnlyContent(new byte[32]);
            if (knownLength)
            {
                content.Headers.ContentLength = 32;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PackageHelpers(client).FetchAndHashAsync(Direct.Url, 16, CancellationToken.None));
    }

    [Fact]
    public async Task Fetch_rejects_an_empty_download()
    {
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamOnlyContent([]),
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new PackageHelpers(client).FetchAndHashAsync(Direct.Url, 16, CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.Forbidden, null)]
    public async Task Winget_lookup_distinguishes_missing_packages_from_unavailable_service(HttpStatusCode status, bool? exists)
    {
        using var client = new HttpClient(new StubHandler(request =>
        {
            Assert.Equal("https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/v/Valve/Steam", request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(status);
        }));
        var result = await new PackageHelpers(client).LookupWingetAsync("Valve.Steam", CancellationToken.None);
        Assert.Equal(exists, result.Exists);
    }

    [Fact]
    public async Task Winget_lookup_and_catalog_verify_succeed_offline()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new HttpRequestException("Offline")));
        var helpers = new PackageHelpers(client);
        Assert.Null((await helpers.LookupWingetAsync("Valve.Steam", CancellationToken.None)).Exists);
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        store.Upsert(new CatalogEntry { Id = "steam", Name = "Steam", Agent = new WingetPackageDefinition("Valve.Steam", "machine") });
        using var output = new StringWriter();
        Assert.Equal(0, await CatalogCli.RunAsync(["catalog", "verify"], store, new FakeAction1Client(), output, CancellationToken.None, helpers));
        Assert.Contains("unavailable", output.ToString());
        Assert.DoesNotContain("->", output.ToString());
    }

    [Fact]
    public async Task Catalog_verify_rejects_an_invalid_stored_hash()
    {
        using var test = new TestDatabase();
        var store = new CatalogStore(test.Database, "");
        store.Upsert(new CatalogEntry { Id = "vendor", Name = "Vendor", Agent = Direct });
        using var connection = test.Database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE catalog_packages SET definition_json = @definition WHERE engine = 'agent';";
        command.Parameters.AddWithValue("@definition", JsonSerializer.Serialize<PackageDefinition>(Direct with { Sha256 = "bad" }, Json));
        command.ExecuteNonQuery();
        using var output = new StringWriter();
        Assert.Equal(1, await CatalogCli.RunAsync(["catalog", "verify"], store, new FakeAction1Client(), output, CancellationToken.None));
        Assert.Contains("sha256", output.ToString());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class StreamOnlyContent(byte[] data) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(data));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => throw new InvalidOperationException("The response must be streamed, not buffered.");
    }
}
