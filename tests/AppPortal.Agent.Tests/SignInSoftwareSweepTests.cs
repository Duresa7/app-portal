using System.Net;

using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

/// <summary>
/// The sweep of a person's own profile when they sign in. Without it their Installed list was as old as
/// the last install the portal did for them, and after a restart it described the PC before it.
/// </summary>
public sealed class SignInSoftwareSweepTests : IDisposable
{
    private const string Alice = @"PC\alice";
    private const string Bob = @"PC\bob";
    private const string Scoop = @"C:\Users\alice\scoop\shims\scoop.cmd";

    private const string Table = """
        Name                           Id                    Version
        -----------------------------------------------------------------
        7-Zip 24.09 (x64)              7zip.7zip             24.09
        """;

    private static readonly PortalSettings Enrolled = new() { ServerUrl = "https://portal.example", DeviceToken = "apd_test" };
    private static readonly DateTimeOffset Start = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
    private readonly string _winget;
    private readonly Server _server = new();
    private PortalSettings _settings = Enrolled;

    public SignInSoftwareSweepTests()
    {
        var package = Path.Combine(_root, "Microsoft.DesktopAppInstaller_1.29.379.0_x64__8wekyb3d8bbwe");
        Directory.CreateDirectory(package);
        _winget = Path.Combine(package, "winget.exe");
        File.WriteAllText(_winget, "");
    }

    [Fact]
    public async Task An_account_that_has_just_signed_in_is_left_to_settle()
    {
        var sessions = new FakeSessions(Alice) { Result = new ProcessResult(0, Table) };
        var sweep = Sweep(sessions);

        Assert.Empty(await sweep.PollAsync(Start, CancellationToken.None));
        Assert.Empty(await sweep.PollAsync(Start + Settle - TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Empty(sessions.Started);
        Assert.Empty(_server.Queries);
    }

    [Fact]
    public async Task A_settled_account_has_its_winget_list_and_its_managers_reported_in_its_session()
    {
        var sessions = new FakeSessions(Alice) { Result = new ProcessResult(0, Table) };
        var sweep = Sweep(sessions);

        await sweep.PollAsync(Start, CancellationToken.None);
        Assert.Equal([Alice], await sweep.PollAsync(Start + Settle, CancellationToken.None));

        // Both lists run as the person, because software in a profile is invisible from outside it.
        Assert.Equal([(Alice, _winget, "list --accept-source-agreements --disable-interactivity"), (Alice, Scoop, "list")],
            sessions.Started);
        Assert.Equal(["?account=PC%5Calice", "?account=PC%5Calice&source=scoop"], _server.Queries);
    }

    [Fact]
    public async Task An_account_that_stays_signed_in_is_swept_once()
    {
        var sessions = new FakeSessions(Alice) { Result = new ProcessResult(0, Table) };
        var sweep = Sweep(sessions);

        await sweep.PollAsync(Start, CancellationToken.None);
        await sweep.PollAsync(Start + Settle, CancellationToken.None);
        Assert.Empty(await sweep.PollAsync(Start + Settle * 2, CancellationToken.None));
        Assert.Empty(await sweep.PollAsync(Start + TimeSpan.FromHours(8), CancellationToken.None));

        Assert.Equal(2, sessions.Started.Count);
    }

    [Fact]
    public async Task Signing_out_and_back_in_is_a_new_sign_in()
    {
        var sessions = new FakeSessions(Alice) { Result = new ProcessResult(0, Table) };
        var sweep = Sweep(sessions);
        await sweep.PollAsync(Start, CancellationToken.None);
        await sweep.PollAsync(Start + Settle, CancellationToken.None);

        sessions.SignedIn = [];
        await sweep.PollAsync(Start + TimeSpan.FromMinutes(5), CancellationToken.None);
        sessions.SignedIn = [Alice];
        var back = Start + TimeSpan.FromMinutes(10);
        Assert.Empty(await sweep.PollAsync(back, CancellationToken.None));

        Assert.Equal([Alice], await sweep.PollAsync(back + Settle, CancellationToken.None));
    }

    [Fact]
    public async Task Whoever_is_signed_in_when_the_service_starts_is_swept()
    {
        // The first poll is the service starting, with the person already at the PC: an upgrade
        // restarts the service under their feet.
        var sessions = new FakeSessions(Alice) { Result = new ProcessResult(0, Table) };
        var sweep = Sweep(sessions);

        await sweep.PollAsync(Start, CancellationToken.None);

        Assert.Equal([Alice], await sweep.PollAsync(Start + Settle, CancellationToken.None));
    }

    [Fact]
    public async Task Two_accounts_are_swept_in_the_order_they_arrived()
    {
        var sessions = new FakeSessions(Bob) { Result = new ProcessResult(0, Table) };
        var sweep = Sweep(sessions);
        await sweep.PollAsync(Start, CancellationToken.None);
        sessions.SignedIn = [Alice, Bob];
        await sweep.PollAsync(Start + TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal([Bob, Alice], await sweep.PollAsync(Start + TimeSpan.FromSeconds(90), CancellationToken.None));
    }

    [Fact]
    public async Task A_pc_that_is_not_enrolled_waits_and_sweeps_once_it_is()
    {
        var sessions = new FakeSessions(Alice) { Result = new ProcessResult(0, Table) };
        var sweep = Sweep(sessions);
        _settings = new PortalSettings();

        await sweep.PollAsync(Start, CancellationToken.None);
        Assert.Empty(await sweep.PollAsync(Start + Settle, CancellationToken.None));
        Assert.Empty(sessions.Started);

        _settings = Enrolled;
        Assert.Equal([Alice], await sweep.PollAsync(Start + Settle * 2, CancellationToken.None));
    }

    [Fact]
    public async Task An_account_is_one_account_whatever_its_case()
    {
        var sessions = new FakeSessions(Alice) { Result = new ProcessResult(0, Table) };
        var sweep = Sweep(sessions);
        await sweep.PollAsync(Start, CancellationToken.None);
        await sweep.PollAsync(Start + Settle, CancellationToken.None);

        // Windows compares account names without regard to case, and a session list may spell one
        // differently from the last.
        sessions.SignedIn = [@"pc\ALICE"];

        Assert.Empty(await sweep.PollAsync(Start + Settle * 2, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SignInSoftwareSweep Sweep(FakeSessions sessions)
    {
        var reporter = new SoftwareReporter(_server.Client(), new NoProcesses(), NullLogger<SoftwareReporter>.Instance, sessions,
            new WingetLocator(_root, _ => null), new ScoopOnly());
        return new SignInSoftwareSweep(reporter, sessions, NullLogger<SignInSoftwareSweep>.Instance, () => _settings,
            settleTime: Settle);
    }

    private sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
            => throw new InvalidOperationException("A profile sweep runs in the session, not as SYSTEM.");
    }

    /// <summary>Scoop in the person's profile, and no other manager anywhere.</summary>
    private sealed class ScoopOnly : IPackageManagerLocator
    {
        public string? Find(PackageManagerDescriptor manager, string? account)
            => manager.Name == "scoop" && account is not null ? Scoop : null;
    }

    /// <summary>Records the query string of each software report.</summary>
    private sealed class Server : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];

        public HttpClient Client() => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/agent/software")
            {
                Queries.Add(request.RequestUri.Query);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
