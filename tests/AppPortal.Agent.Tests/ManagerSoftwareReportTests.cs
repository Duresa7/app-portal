using System.Net;
using System.Net.Http.Json;

using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

/// <summary>
/// What a package manager installed, reported as its own list. Winget cannot see any of it, so without
/// these reports a Scoop app never shows as installed and its Remove button never appears.
/// </summary>
public sealed class ManagerSoftwareReportTests
{
    private static readonly PortalSettings Enrolled = new() { ServerUrl = "https://portal.example", DeviceToken = "apd_test" };

    private static PackageManagerDescriptor Manager(string name) => PackageManagers.Find(name)!;

    [Fact]
    public async Task A_machine_wide_list_goes_to_the_server_under_its_managers_name()
    {
        var server = new Server();
        var processes = new Processes(new ProcessResult(0, "git|2.46.0\r\n7zip|24.08\r\n"));
        var locator = new Locator { [("choco", null)] = @"C:\ProgramData\chocolatey\bin\choco.exe" };

        await Reporter(server, processes, locator).ReportManagerAsync(Enrolled, Manager("choco"), CancellationToken.None);

        Assert.Equal((@"C:\ProgramData\chocolatey\bin\choco.exe", "list --limit-output"), Assert.Single(processes.Started));
        var (query, software) = Assert.Single(server.Reports);
        Assert.Equal("?source=choco", query);
        Assert.Equal(["git", "7zip"], software.Select(item => item.Name));
    }

    [Fact]
    public async Task A_profile_list_is_read_inside_that_persons_session_and_names_them()
    {
        var server = new Server();
        var sessions = new FakeSessions(@"CORP\ada") { Result = new ProcessResult(0, "Name Version\n---- -------\n7zip 24.08\n") };
        var locator = new Locator { [("scoop", @"CORP\ada")] = @"C:\Users\ada\scoop\shims\scoop.cmd" };

        await Reporter(server, new Processes(new ProcessResult(1, "not as SYSTEM")), locator, sessions)
            .ReportManagerAsync(Enrolled, Manager("scoop"), CancellationToken.None, @"CORP\ada");

        Assert.Equal((@"CORP\ada", @"C:\Users\ada\scoop\shims\scoop.cmd", "list"), Assert.Single(sessions.Started));
        var (query, software) = Assert.Single(server.Reports);
        Assert.Equal("?account=CORP%5Cada&source=scoop", query);
        Assert.Equal("7zip", Assert.Single(software).Name);
    }

    [Fact]
    public async Task A_list_that_failed_is_not_sent_but_an_empty_one_that_worked_is()
    {
        // The failed one says nothing about what is installed. The empty one says Chocolatey has
        // nothing, which the server must hear so the last package removed leaves the record.
        var locator = new Locator { [("choco", null)] = @"C:\choco.exe" };

        var failed = new Server();
        await Reporter(failed, new Processes(new ProcessResult(1, "Chocolatey is broken")), locator)
            .ReportManagerAsync(Enrolled, Manager("choco"), CancellationToken.None);
        Assert.Empty(failed.Reports);

        var empty = new Server();
        await Reporter(empty, new Processes(new ProcessResult(0, "")), locator)
            .ReportManagerAsync(Enrolled, Manager("choco"), CancellationToken.None);
        Assert.Empty(Assert.Single(empty.Reports).Software);
    }

    [Fact]
    public async Task A_manager_that_is_not_on_the_pc_is_not_asked()
    {
        var server = new Server();
        var processes = new Processes(new ProcessResult(0, ""));

        await Reporter(server, processes, new Locator()).ReportManagersAsync(Enrolled, CancellationToken.None);

        Assert.Empty(processes.Started);
        Assert.Empty(server.Reports);
    }

    [Fact]
    public async Task The_machine_sweep_asks_only_managers_that_install_for_everyone()
    {
        // Cargo, Bun and .NET tools only ever install into a profile. As SYSTEM they would list the
        // service's own profile, which nobody uses.
        var locator = new Locator
        {
            [("cargo", null)] = @"C:\cargo.exe",
            [("pip", null)] = @"C:\Python\Scripts\pip.exe",
        };
        var processes = new Processes(new ProcessResult(0, "[]"));

        await Reporter(new Server(), processes, locator).ReportManagersAsync(Enrolled, CancellationToken.None);

        Assert.Equal(@"C:\Python\Scripts\pip.exe", Assert.Single(processes.Started).File);
    }

    [Fact]
    public async Task The_daily_report_lists_each_manager_for_the_machine_and_for_each_person_signed_in()
    {
        var server = new Server();
        var locator = new Locator
        {
            [("choco", null)] = @"C:\choco.exe",
            [("cargo", @"CORP\ada")] = @"C:\Users\ada\.cargo\bin\cargo.exe",
        };
        var sessions = new FakeSessions(@"CORP\ada") { Result = new ProcessResult(0, "ripgrep v14.1.0:\n    rg.exe\n") };
        var processes = new Processes(new ProcessResult(0, "git|2.46.0"));
        using var http = server.Client();
        var software = new SoftwareReporter(http, processes, NullLogger<SoftwareReporter>.Instance, sessions, managers: locator);
        var reporter = new ManagerReporter(http, processes, locator, NullLogger<ManagerReporter>.Instance, sessions,
            () => Enrolled, software: software);

        Assert.True(await reporter.ReportAsync(CancellationToken.None));

        Assert.Contains(server.Reports, report => report.Query == "?source=choco");
        Assert.Contains(server.Reports, report => report.Query == "?account=CORP%5Cada&source=cargo"
                                                  && report.Software.Single().Name == "ripgrep");
    }

    private static SoftwareReporter Reporter(Server server, Processes processes, Locator locator, FakeSessions? sessions = null)
        => new(server.Client(), processes, NullLogger<SoftwareReporter>.Instance, sessions ?? new FakeSessions(), managers: locator);

    private sealed class Locator : Dictionary<(string Manager, string? Account), string>, IPackageManagerLocator
    {
        public string? Find(PackageManagerDescriptor manager, string? account)
            => TryGetValue((manager.Name, account), out var path) ? path : null;
    }

    private sealed class Processes(ProcessResult result) : IProcessRunner
    {
        public List<(string File, string Arguments)> Started { get; } = [];

        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            Started.Add((file, arguments));
            return Task.FromResult(result);
        }
    }

    /// <summary>Records each software report by its query string. Everything else it answers blank.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public List<(string Query, List<InstalledSoftware> Software)> Reports { get; } = [];

        public HttpClient Client() => new(this, disposeHandler: false);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/agent/software")
            {
                Reports.Add((request.RequestUri.Query,
                    (await request.Content!.ReadFromJsonAsync<List<InstalledSoftware>>(cancellationToken))!));
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
