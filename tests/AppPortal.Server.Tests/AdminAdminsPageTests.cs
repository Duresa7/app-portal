using System.Net;

using AppPortal.Server.Admin;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class AdminAdminsPageTests : IDisposable
{
    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;

    public AdminAdminsPageTests()
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
    }

    [Fact]
    public async Task The_page_is_closed_to_anyone_not_signed_in()
    {
        var response = await TestDatabase.Browser(_factory).GetAsync("/admin/admins");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/admin/login", response.Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_page_opens_for_a_signed_in_administrator_and_lists_every_account()
    {
        new AdminStore(_test.Database).Add("second", TestDatabase.AdminPassword);

        var response = await (await TestDatabase.SignedIn(_factory)).GetAsync("/admin/admins");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(">admin<", html, StringComparison.Ordinal);
        Assert.Contains(">second<", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Disabling_through_htmx_answers_with_the_table_alone()
    {
        new AdminStore(_test.Database).Add("second", TestDatabase.AdminPassword);
        using var admin = await TestDatabase.SignedIn(_factory);
        var token = await TestDatabase.TokenOn(admin, "/admin/admins");
        admin.DefaultRequestHeaders.Add("HX-Request", "true");

        var response = await admin.PostAsync("/admin/admins?handler=Disable", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "second",
            ["__RequestVerificationToken"] = token,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"admin-table\"", body, StringComparison.Ordinal);
        Assert.True(new AdminStore(_test.Database).Find("second")!.Disabled);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
