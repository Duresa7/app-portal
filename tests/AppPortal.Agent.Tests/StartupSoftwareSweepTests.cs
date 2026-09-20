using System.Net;
using System.Net.Http.Json;

using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

/// <summary>
/// The sweep at service start. It matters at two moments nothing else covers: after a restart, where
/// the install that asked for the restart is checked against what the PC carries now rather than what
/// it carried before, and after an upgrade, where a device whose only engine is the agent would
/// otherwise show an empty Installed list until somebody installed something through the portal.
/// </summary>
public sealed class StartupSoftwareSweepTests : IDisposable
{
    private const string Table = """
        Name                           Id                    Version
        -----------------------------------------------------------------
        7-Zip 24.09 (x64)              7zip.7zip             24.09
        Valorant                       RiotGames.Valorant    1.0.0
        """;

    private static readonly PortalSettings Enrolled = new() { ServerUrl = "https://portal.example", DeviceToken = "apd_test" };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));

    public StartupSoftwareSweepTests()
    {
        var package = Path.Combine(_root, "Microsoft.DesktopAppInstaller_1.22.11141.0_x64__8wekyb3d8bbwe");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "winget.exe"), "");
    }

    [Fact]
    public async Task Starting_the_service_tells_the_server_what_is_installed()
    {
        var posted = new List<InstalledSoftware>();
        var handler = new Capture(posted);
        var processes = new Recording(Table);

        await RunAsync(handler, processes, Enrolled);

        // Machine-wide, without an account: the sweep runs as the service does, and software in one
        // person's profile is invisible from out here. Theirs is reported when their install finishes.
        Assert.Equal("list --accept-source-agreements --disable-interactivity", processes.Arguments);
        Assert.Equal("/api/v1/agent/software", handler.Path);
        Assert.Empty(handler.Query);
        Assert.Equal(["7-Zip 24.09 (x64)", "Valorant"], posted.Select(item => item.Name));
    }

    [Fact]
    public async Task A_device_that_has_not_enrolled_yet_asks_nothing()
    {
        var processes = new Recording(Table);

        await RunAsync(new Capture([]), processes, new PortalSettings());

        // No token to send it with, and nobody to send it to. The install that enrolls this PC sweeps
        // when it finishes, so nothing is lost by staying quiet here.
        Assert.Null(processes.Arguments);
    }

    [Fact]
    public async Task An_unreadable_list_leaves_the_last_report_alone()
    {
        var posted = new List<InstalledSoftware>();
        var handler = new Capture(posted);

        // Reporting nothing would clear what the server holds, and a device that answered oddly once is
        // not a device with no software on it.
        await RunAsync(handler, new Recording("winget is having a bad day"), Enrolled);

        Assert.Null(handler.Path);
        Assert.Empty(posted);
    }

    private async Task RunAsync(Capture handler, Recording processes, PortalSettings settings)
    {
        using var http = new HttpClient(handler);
        var reporter = new SoftwareReporter(http, processes, NullLogger<SoftwareReporter>.Instance,
            locator: new WingetLocator(_root));
        using var sweep = new StartupSoftwareSweep(reporter, NullLogger<StartupSoftwareSweep>.Instance, () => settings);

        await sweep.StartAsync(CancellationToken.None);
        await sweep.ExecuteTask!;
        await sweep.StopAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class Recording(string output) : IProcessRunner
    {
        public string? Arguments { get; private set; }

        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            Arguments = arguments;
            return Task.FromResult(new ProcessResult(0, output));
        }
    }

    private sealed class Capture(List<InstalledSoftware> posted) : HttpMessageHandler
    {
        public string? Path { get; private set; }

        public string Query { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Query = request.RequestUri.Query;
            posted.AddRange(await request.Content!.ReadFromJsonAsync<List<InstalledSoftware>>(ct) ?? []);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
