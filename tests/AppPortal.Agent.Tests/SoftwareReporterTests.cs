using System.Net;
using System.Net.Http.Json;

using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

/// <summary>
/// The winget sweep after an install. On a fresh Windows 11 PC it ran winget.exe without its framework
/// packages, winget died before printing anything, and the person's Installed list stayed empty.
/// </summary>
public sealed class SoftwareReporterTests : IDisposable
{
    private static readonly PortalSettings Enrolled = new() { ServerUrl = "https://portal.example", DeviceToken = "apd_test" };

    /// <summary>What winget printed in a fresh account's session, the Store's notice included.</summary>
    private const string Transcript = """
        The `msstore` source requires that you view the following agreements before using.
        Terms of Transaction: https://aka.ms/microsoft-store-terms-of-transaction

        Name                        Id                                       Version Source
        -----------------------------------------------------------------------------------
        App Portal Proof proof-user ARP\User\X64\AppPortalProof-proof-user   1.0.0
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));

    public SoftwareReporterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task A_profile_sweep_runs_winget_with_its_framework_packages_and_names_the_account()
    {
        var winget = Path.Combine(_root, "Microsoft.DesktopAppInstaller_1.29.379.0_x64__8wekyb3d8bbwe");
        Directory.CreateDirectory(winget);
        File.WriteAllText(Path.Combine(winget, "winget.exe"), "");
        File.WriteAllText(Path.Combine(winget, "AppxManifest.xml"), """
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Identity Name="Microsoft.DesktopAppInstaller" ProcessorArchitecture="x64" />
              <Dependencies><PackageDependency Name="Microsoft.VCLibs.140.00.UWPDesktop" /></Dependencies>
            </Package>
            """);
        var vclibs = Path.Combine(_root, "Microsoft.VCLibs.140.00.UWPDesktop_14.0.33728.0_x64__8wekyb3d8bbwe");
        Directory.CreateDirectory(vclibs);
        var sessions = new FakeSessions(@"PROOF-PC\apptester") { Result = new ProcessResult(0, Transcript) };
        var server = new Server();

        await new SoftwareReporter(server.Client(), new NoProcesses(), NullLogger<SoftwareReporter>.Instance, sessions,
                new WingetLocator(_root))
            .ReportAsync(Enrolled, CancellationToken.None, @"PROOF-PC\apptester");

        Assert.Equal<string>([vclibs], Assert.Single(sessions.PathFirst));
        var (query, software) = Assert.Single(server.Reports);
        Assert.Equal("?account=PROOF-PC%5Capptester", query);
        Assert.Equal("App Portal Proof proof-user", Assert.Single(software).Name);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("\r\n\r\n  Failed in attempting to update the source: winget  \r\nmore", "Failed in attempting to update the source: winget")]
    public void The_log_line_carries_the_first_thing_winget_said(string output, string expected)
    {
        Assert.Equal(expected, SoftwareReporter.FirstLine(output));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
            => throw new InvalidOperationException("A profile sweep runs in the session, not as SYSTEM.");
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
