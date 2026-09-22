using System.Net;
using System.Net.Http.Json;

using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

public sealed class ManagerReporterTests
{
    private static readonly PortalSettings Settings = new() { ServerUrl = "https://portal.example", DeviceToken = "apd_test" };

    [Fact]
    public async Task A_manager_on_the_pc_is_reported_with_the_first_line_it_prints_about_itself()
    {
        var locator = new FakeLocator { [("choco", null)] = @"C:\ProgramData\chocolatey\bin\choco.exe" };
        var processes = new FakeProcesses(_ => new ProcessResult(0, "2.3.0\r\nChocolatey v2.3.0\r\n"));

        var found = await Reporter(locator, processes).DetectAsync(CancellationToken.None);

        var choco = Assert.Single(found);
        Assert.Equal(new DeviceManager("choco", "2.3.0"), choco);
        Assert.Equal((@"C:\ProgramData\chocolatey\bin\choco.exe", "--version"), Assert.Single(processes.Started));
    }

    [Fact]
    public async Task A_manager_that_is_on_disk_but_will_not_answer_is_not_reported()
    {
        // The executable exists and fails when asked its own version. An install through it would fail
        // the same way, so reporting it would be telling an administrator something untrue.
        var locator = new FakeLocator
        {
            [("choco", null)] = @"C:\choco\choco.exe",
            [("npm", null)] = @"C:\nodejs\npm.cmd",
        };
        var processes = new FakeProcesses(file => file.EndsWith("npm.cmd", StringComparison.Ordinal)
            ? throw new TimeoutException("npm.cmd did not finish.")
            : new ProcessResult(1, "broken"));

        Assert.Empty(await Reporter(locator, processes).DetectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_manager_in_somebodys_profile_is_reported_for_them_and_nobody_else()
    {
        var locator = new FakeLocator { [("scoop", @"CORP\ada")] = @"C:\Users\ada\scoop\shims\scoop.cmd" };
        var processes = new FakeProcesses(_ => throw new InvalidOperationException("No version is asked for in a profile."));

        var found = await Reporter(locator, processes, new FakeSessions(@"CORP\ada")).DetectAsync(CancellationToken.None);

        Assert.Equal(new DeviceManager("scoop", "", @"CORP\ada"), Assert.Single(found));
    }

    [Fact]
    public async Task A_machine_wide_manager_is_not_reported_again_for_each_person_who_can_see_it()
    {
        // The profile search falls back to the machine's PATH, so it finds the same file. One row, not
        // one row per person signed in.
        var locator = new FakeLocator
        {
            [("npm", null)] = @"C:\Program Files\nodejs\npm.cmd",
            [("npm", @"CORP\ada")] = @"C:\Program Files\nodejs\npm.cmd",
        };

        var found = await Reporter(locator, new FakeProcesses(_ => new ProcessResult(0, "10.8.2")), new FakeSessions(@"CORP\ada"))
            .DetectAsync(CancellationToken.None);

        Assert.Equal(new DeviceManager("npm", "10.8.2"), Assert.Single(found));
    }

    [Fact]
    public async Task A_manager_that_only_installs_machine_wide_is_not_looked_for_in_a_profile()
    {
        var locator = new FakeLocator();
        await Reporter(locator, new FakeProcesses(_ => new ProcessResult(0, "")), new FakeSessions(@"CORP\ada"))
            .DetectAsync(CancellationToken.None);

        Assert.DoesNotContain(locator.Asked, asked => asked is ("choco", not null) or ("vcpkg", not null));
        Assert.Contains(locator.Asked, asked => asked == ("scoop", @"CORP\ada"));
    }

    [Fact]
    public async Task The_whole_list_goes_to_the_server_with_the_device_token()
    {
        List<DeviceManager>? sent = null;
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.Equal("https://portal.example/api/v1/agent/managers", request.RequestUri!.AbsoluteUri);
            Assert.Equal("apd_test", request.Headers.Authorization!.Parameter);
            sent = await request.Content!.ReadFromJsonAsync<List<DeviceManager>>();
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        var locator = new FakeLocator { [("pip", null)] = @"C:\Python\Scripts\pip.exe" };

        Assert.True(await Reporter(locator, new FakeProcesses(_ => new ProcessResult(0, "pip 24.2 from C:\\Python")), http: http)
            .ReportAsync(CancellationToken.None));
        Assert.Equal(new DeviceManager("pip", "pip 24.2 from C:\\Python"), Assert.Single(sent!));
    }

    [Fact]
    public async Task A_pc_that_has_not_enrolled_yet_reports_nothing_and_says_so()
    {
        using var http = new HttpClient(new Handler(_ => throw new InvalidOperationException("Nothing may be sent.")));
        var reporter = new ManagerReporter(http, new FakeProcesses(_ => new ProcessResult(0, "")), new FakeLocator(),
            NullLogger<ManagerReporter>.Instance, settings: () => new PortalSettings());

        Assert.False(await reporter.ReportAsync(CancellationToken.None));
    }

    private static ManagerReporter Reporter(FakeLocator locator, FakeProcesses processes, FakeSessions? sessions = null, HttpClient? http = null)
        => new(http ?? new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)))),
            processes, locator, NullLogger<ManagerReporter>.Instance, sessions ?? new FakeSessions(), () => Settings);

    private sealed class FakeLocator : Dictionary<(string Manager, string? Account), string>, IPackageManagerLocator
    {
        public List<(string Manager, string? Account)> Asked { get; } = [];

        public string? Find(PackageManagerDescriptor manager, string? account)
        {
            Asked.Add((manager.Name, account));
            return TryGetValue((manager.Name, account), out var path) ? path : null;
        }
    }

    private sealed class FakeProcesses(Func<string, ProcessResult> run) : IProcessRunner
    {
        public List<(string File, string Arguments)> Started { get; } = [];

        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        {
            Started.Add((file, arguments));
            return Task.FromResult(run(file));
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handle(request);
    }
}
