using System.Net;
using System.Text.RegularExpressions;

using AppPortal.Server.Admin;
using AppPortal.Server.Devices;
using AppPortal.Server.Installs;
using AppPortal.Shared;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AppPortal.Server.Tests;

public sealed class AdminInstallsPageTests : IDisposable
{
    private const string Password = "a-long-enough-password";

    private readonly TestDatabase _test = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly InstallStore _installs;

    public AdminInstallsPageTests()
    {
        var catalogPath = Path.Combine(_test.Root, "catalog.json");
        File.WriteAllText(catalogPath, """{ "apps": [] }""");
        new DeviceStore(_test.Database).Add("PC-A", "endpoint-a");
        new DeviceStore(_test.Database).Add("PC-B", "endpoint-b");
        _installs = new InstallStore(_test.Database);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Action1:Mode", "Fake");
            builder.UseSetting("Portal:CatalogPath", catalogPath);
            builder.UseSetting("Portal:DataDirectory", _test.DataDirectory);
            builder.UseSetting("Portal:StatusPollSeconds", "3600");
        });

        new AdminStore(_test.Database).Add("admin", Password);
    }

    private async Task<HttpClient> SignedIn()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var html = await client.GetStringAsync("/admin/login");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        var response = await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "admin",
            ["Password"] = Password,
            ["__RequestVerificationToken"] = token,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private InstallRecord Seed(string device, string appId, InstallState state, DateTimeOffset at, string? by = null)
    {
        var record = new InstallRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            DeviceName = device,
            AppId = appId,
            AppName = appId.ToUpperInvariant(),
            State = state,
            RequestedAt = at,
            CompletedAt = state is InstallState.Queued or InstallState.Running ? null : at.AddMinutes(1),
            RequestedBy = by,
            Detail = state == InstallState.Failed ? "The installer returned exit code 1603." : null,
            AutomationId = "automation-" + appId,
        };
        Assert.True(_installs.Upsert(record));
        return record;
    }

    [Fact]
    public async Task The_page_lists_installs_with_the_requester_and_the_engine()
    {
        Seed("PC-A", "chrome", InstallState.Succeeded, DateTimeOffset.UtcNow.AddHours(-1), @"CONTOSO\jdoe");
        var admin = await SignedIn();

        var html = await admin.GetStringAsync("/admin/installs");

        Assert.Contains("CHROME", html);
        Assert.Contains("PC-A", html);
        Assert.Contains(@"CONTOSO\jdoe", html);
        Assert.Contains("via Action1", html);
    }

    [Fact]
    public async Task Filtering_by_state_in_the_url_shows_only_that_state()
    {
        Seed("PC-A", "chrome", InstallState.Failed, DateTimeOffset.UtcNow.AddHours(-2));
        Seed("PC-B", "vlc", InstallState.Succeeded, DateTimeOffset.UtcNow.AddHours(-1));
        var admin = await SignedIn();

        var html = await admin.GetStringAsync("/admin/installs?State=Failed");

        Assert.Contains("CHROME", html);
        Assert.DoesNotContain("VLC", html);
    }

    [Fact]
    public async Task A_filtered_page_with_nothing_on_it_says_so()
    {
        Seed("PC-A", "chrome", InstallState.Succeeded, DateTimeOffset.UtcNow);
        var admin = await SignedIn();

        var html = await admin.GetStringAsync("/admin/installs?Device=PC-B");

        Assert.Contains("No installs match", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_quiet_page_discovers_new_installs_and_keeps_polling_after_completion(bool hasHistory)
    {
        if (hasHistory)
        {
            Seed("PC-A", "old", InstallState.Succeeded, DateTimeOffset.UtcNow.AddHours(-1));
        }

        var admin = await SignedIn();
        var html = await admin.GetStringAsync("/admin/installs?Device=PC-A");
        Assert.Contains("hx-trigger=\"every 30s\"", html);
        var pollUrl = WebUtility.HtmlDecode(Regex.Match(html, "hx-get=\"([^\"]+)\"").Groups[1].Value);
        Assert.Contains("Device=PC-A", pollUrl);

        var active = Seed("PC-A", "chrome", InstallState.Running, DateTimeOffset.UtcNow, @"CONTOSO\jdoe");
        Seed("PC-B", "vlc", InstallState.Running, DateTimeOffset.UtcNow);
        var running = await admin.GetStringAsync(pollUrl);
        Assert.Contains("CHROME", running);
        Assert.Contains(@"CONTOSO\jdoe", running);
        Assert.DoesNotContain("VLC", running);
        Assert.Contains("hx-trigger=\"every 30s\"", running);

        active.State = InstallState.Succeeded;
        active.CompletedAt = DateTimeOffset.UtcNow;
        Assert.True(_installs.Upsert(active));
        var completed = await admin.GetStringAsync(pollUrl);
        Assert.Contains("Succeeded", completed);
        Assert.Contains("hx-trigger=\"every 30s\"", completed);

        Seed("PC-A", "firefox", InstallState.Queued, DateTimeOffset.UtcNow);
        var next = await admin.GetStringAsync(pollUrl);
        Assert.Contains("FIREFOX", next);
    }

    [Fact]
    public async Task The_rows_handler_returns_the_table_alone_and_keeps_the_filter()
    {
        Seed("PC-A", "chrome", InstallState.Running, DateTimeOffset.UtcNow.AddMinutes(-2));
        Seed("PC-B", "vlc", InstallState.Running, DateTimeOffset.UtcNow.AddMinutes(-1));
        var admin = await SignedIn();

        var html = await admin.GetStringAsync("/admin/installs?handler=Rows&Device=PC-A");

        Assert.DoesNotContain("<html", html);
        Assert.Contains("install-rows", html);
        Assert.Contains("CHROME", html);
        Assert.DoesNotContain("VLC", html);
    }

    [Fact]
    public async Task The_detail_page_shows_every_stored_field_and_the_external_reference()
    {
        var record = Seed("PC-A", "chrome", InstallState.Failed, DateTimeOffset.UtcNow.AddHours(-1), @"CONTOSO\jdoe");
        var admin = await SignedIn();

        var html = await admin.GetStringAsync($"/admin/installs/{record.Id}");

        Assert.Contains(record.Id, html);
        Assert.Contains("CHROME", html);
        Assert.Contains("PC-A", html);
        Assert.Contains(@"CONTOSO\jdoe", html);
        Assert.Contains("via Action1", html);
        Assert.Contains("automation-chrome", html);
        Assert.Contains("endpoint-a", html);
        Assert.Contains("exit code 1603", html);
    }

    [Fact]
    public async Task An_unknown_install_is_not_found()
    {
        var admin = await SignedIn();

        var response = await admin.GetAsync("/admin/installs/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task The_dashboard_counts_installs_today_failures_this_week_and_active_now()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("PC-A", "a", InstallState.Failed, now.AddHours(-2));
        Seed("PC-A", "b", InstallState.Failed, now.AddDays(-30));
        Seed("PC-B", "c", InstallState.Running, now.AddMinutes(-5));
        var admin = await SignedIn();

        var html = await admin.GetStringAsync("/admin");

        Assert.Contains("Failures this week", html);
        Assert.Contains("Active now", html);
        // One failure inside seven days, not the thirty-day-old one.
        Assert.Matches(@"<div class=""value bad"">1</div>\s*<div class=""label"">Failures this week</div>", html);
        Assert.Matches(@"<div class=""value"">1</div>\s*<div class=""label"">Active now</div>", html);
    }

    [Fact]
    public async Task Installs_today_counts_an_install_made_today_wherever_the_server_sits()
    {
        // Just after local midnight is where a UTC boundary and a local one disagree, and where the
        // tile used to read zero on a day that already had installs.
        Seed("PC-A", "a", InstallState.Succeeded, DateTimeOffset.Now);
        var admin = await SignedIn();

        var html = await admin.GetStringAsync("/admin");

        Assert.Matches(@"<div class=""value"">1</div>\s*<div class=""label"">Installs today</div>", html);
    }

    [Fact]
    public async Task The_installs_pages_are_closed_without_a_session()
    {
        var anonymous = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        foreach (var path in new[] { "/admin/installs", "/admin/installs/anything" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/admin/login", response.Headers.Location?.OriginalString);
        }
    }

    [Fact]
    public async Task Removed_devices_keep_their_history_count_filter_and_pagination()
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 101; i++)
        {
            Seed("PC-A", $"retired-{i}", InstallState.Succeeded, now.AddMinutes(-i));
        }

        Seed("PC-B", "other-device", InstallState.Succeeded, now);
        Assert.True(new DeviceStore(_test.Database).Remove("PC-A"));
        var admin = await SignedIn();

        var all = await admin.GetStringAsync("/admin/installs");
        Assert.Contains("of 102", all);
        var first = await admin.GetStringAsync("/admin/installs?Device=PC-A");
        Assert.Contains("Showing 1 to 100 of 101", first);
        Assert.Contains("Older", first);
        Assert.DoesNotContain("OTHER-DEVICE", first);
        var last = await admin.GetStringAsync("/admin/installs?Device=PC-A&Skip=100");
        Assert.Contains("Showing 101 to 101 of 101", last);
        Assert.Contains("RETIRED-100", last);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _test.Dispose();
    }
}
