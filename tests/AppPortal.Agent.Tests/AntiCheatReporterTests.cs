using System.Net;
using System.Net.Http.Json;

using AppPortal.Agent.Jobs;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

/// <summary>
/// Which of a PC's services are anti-cheat, and the report of them. The whole list goes every time, an
/// empty one included, because a product that was removed has to be able to leave the record.
/// </summary>
public sealed class AntiCheatReporterTests
{
    private static readonly PortalSettings Enrolled = new() { ServerUrl = "https://portal.example", DeviceToken = "apd_test" };

    [Fact]
    public void Only_known_anti_cheat_is_picked_out_of_the_services()
    {
        var found = AntiCheatReporter.Find(
        [
            new ServiceEntry("EventLog", "service", "running", "automatic"),
            new ServiceEntry("vgk", "driver", "stopped", "system"),
            new ServiceEntry("vgc", "service", "running", "manual"),
            new ServiceEntry("BEService", "service", "stopped", "manual"),
            new ServiceEntry("VGK", "driver", "stopped", "system"),
        ]);

        Assert.Equal(
        [
            new DeviceAntiCheat("BattlEye", "BEService", "service", "stopped", "manual"),
            new DeviceAntiCheat("Riot Vanguard", "vgc", "service", "running", "manual"),
            new DeviceAntiCheat("Riot Vanguard", "vgk", "driver", "stopped", "system"),
        ], found);
    }

    [Fact]
    public async Task The_report_posts_what_it_found()
    {
        var server = new Server();
        var reporter = new AntiCheatReporter(server.Client(), new Services(new ServiceEntry("EasyAntiCheat_EOS", "service", "stopped", "manual")),
            NullLogger<AntiCheatReporter>.Instance, () => Enrolled);

        Assert.True(await reporter.ReportAsync(CancellationToken.None));

        var (path, rows) = Assert.Single(server.Reports);
        Assert.Equal("/api/v1/agent/anticheat", path);
        Assert.Equal("Easy Anti-Cheat", Assert.Single(rows).Product);
    }

    [Fact]
    public async Task A_pc_with_none_says_so_rather_than_saying_nothing()
    {
        var server = new Server();
        var reporter = new AntiCheatReporter(server.Client(), new Services(new ServiceEntry("Spooler", "service", "running", "automatic")),
            NullLogger<AntiCheatReporter>.Instance, () => Enrolled);

        Assert.True(await reporter.ReportAsync(CancellationToken.None));

        Assert.Empty(Assert.Single(server.Reports).Rows);
    }

    [Fact]
    public async Task A_pc_that_is_not_enrolled_reports_nothing()
    {
        var server = new Server();
        var reporter = new AntiCheatReporter(server.Client(), new Services(new ServiceEntry("vgk", "driver", "running", "system")),
            NullLogger<AntiCheatReporter>.Instance, () => new PortalSettings());

        Assert.False(await reporter.ReportAsync(CancellationToken.None));

        Assert.Empty(server.Reports);
    }

    [Fact]
    public async Task A_refused_report_is_retried_rather_than_counted()
    {
        var server = new Server(HttpStatusCode.InternalServerError);
        var reporter = new AntiCheatReporter(server.Client(), new Services(), NullLogger<AntiCheatReporter>.Instance, () => Enrolled);

        Assert.False(await reporter.ReportAsync(CancellationToken.None));
    }

    [Fact]
    public void The_real_service_list_has_services_and_drivers_with_their_state()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var services = new WindowsServiceInventory().List();

        // The event log runs on every Windows there is, and so does the file system driver under it.
        var eventLog = Assert.Single(services, s => string.Equals(s.Name, "EventLog", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(("service", "running", "automatic"), (eventLog.Type, eventLog.State, eventLog.StartType));
        Assert.Contains(services, s => s.Type == "driver" && string.Equals(s.Name, "Ntfs", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class Services(params ServiceEntry[] entries) : IServiceInventory
    {
        public IReadOnlyList<ServiceEntry> List() => entries;
    }

    private sealed class Server(HttpStatusCode status = HttpStatusCode.NoContent) : HttpMessageHandler
    {
        public List<(string Path, List<DeviceAntiCheat> Rows)> Reports { get; } = [];

        public HttpClient Client() => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Reports.Add((request.RequestUri!.AbsolutePath,
                (await request.Content!.ReadFromJsonAsync<List<DeviceAntiCheat>>(cancellationToken))!));
            return new HttpResponseMessage(status);
        }
    }
}
