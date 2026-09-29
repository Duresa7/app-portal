using AppPortal.Agent.Executors;
using AppPortal.Agent.Jobs;
using AppPortal.Shared;

using Microsoft.Extensions.Logging.Abstractions;

namespace AppPortal.Agent.Tests;

/// <summary>
/// A game handed to its launcher. Nothing is installed here: the job opens one link on one person's
/// desktop, so what matters is that it is the right link, for the right person, and only when the
/// launcher is there to answer it.
/// </summary>
public sealed class LauncherHandoffExecutorTests : IDisposable
{
    private const string Ada = @"CORP\ada";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));

    public LauncherHandoffExecutorTests() => Directory.CreateDirectory(_root);

    public static TheoryData<string, string, string> Links => new()
    {
        { "steam", "730", "steam://install/730" },
        { "epic", "Fortnite", "com.epicgames.launcher://apps/Fortnite?action=launch" },
        { "gog", "1207658924", "goggalaxy://openGameView/1207658924" },
        { "ubisoft", "5271", "uplay://launch/5271/0" },
    };

    [Theory]
    [MemberData(nameof(Links))]
    public async Task Each_launcher_is_opened_on_its_own_install_link_in_the_persons_session(string launcher, string id, string link)
    {
        var sessions = new FakeSessions(Ada);

        var result = await Executor(sessions, new Registered(Ada)).RunAsync(new JobContext("job-1", Ada),
            new LauncherPackageDefinition(launcher, id), new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal($"Opened in {GameLaunchers.Find(launcher)!.DisplayName}. Finish the install there.", result.Detail);
        var started = Assert.Single(sessions.Started);
        Assert.Equal((Ada, "rundll32.exe", "url.dll,FileProtocolHandler " + link), started);
    }

    [Fact]
    public async Task Without_the_launcher_nothing_opens_and_the_person_is_told_what_to_install()
    {
        var sessions = new FakeSessions(Ada);

        var result = await Executor(sessions, new Registered()).RunAsync(new JobContext("job-2", Ada),
            new LauncherPackageDefinition("steam", "730"), new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.WaitingForUser);
        Assert.Equal(@"Steam is not installed for CORP\ada. Install Steam first.", result.Detail);
        Assert.Empty(sessions.Started);
    }

    [Fact]
    public async Task A_person_who_is_away_is_waited_for_rather_than_read_as_having_no_launcher()
    {
        var protocols = new Registered(Ada);

        var result = await Executor(new FakeSessions(), protocols).RunAsync(new JobContext("job-3", Ada),
            new LauncherPackageDefinition("steam", "730"), new Progress(), CancellationToken.None);

        Assert.True(result.WaitingForUser);
        Assert.Equal(@"Waiting for CORP\ada to sign in.", result.Detail);
        // Their hive is not loaded while they are away, so asking would only have said no.
        Assert.Empty(protocols.Asked);
    }

    [Fact]
    public async Task A_job_that_does_not_say_who_asked_is_refused()
    {
        var result = await Executor(new FakeSessions(Ada), new Registered(Ada)).RunAsync(new JobContext("job-4", null),
            new LauncherPackageDefinition("steam", "730"), new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(result.WaitingForUser);
    }

    [Fact]
    public async Task A_launcher_this_agent_does_not_know_is_refused_by_name()
    {
        var result = await Executor(new FakeSessions(Ada), new Registered(Ada)).RunAsync(new JobContext("job-5", Ada),
            new LauncherPackageDefinition("battlenet", "WoW"), new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("'battlenet'", result.Detail);
    }

    [Fact]
    public async Task Removing_a_steam_game_opens_steams_own_uninstall()
    {
        var sessions = new FakeSessions(Ada);

        var result = await Executor(sessions, new Registered(Ada)).UninstallAsync(new JobContext("job-6", Ada, InstallKind.Uninstall),
            new LauncherPackageDefinition("steam", "570"), new Progress(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("Opened in Steam. Finish the removal there.", result.Detail);
        Assert.Equal("url.dll,FileProtocolHandler steam://uninstall/570", Assert.Single(sessions.Started).Arguments);
    }

    [Fact]
    public async Task A_launcher_with_no_uninstall_link_says_where_to_remove_the_game()
    {
        var sessions = new FakeSessions(Ada);

        var result = await Executor(sessions, new Registered(Ada)).UninstallAsync(new JobContext("job-7", Ada, InstallKind.Uninstall),
            new LauncherPackageDefinition("gog", "1207658924"), new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Remove it in GOG Galaxy.", result.Detail);
        Assert.Empty(sessions.Started);
    }

    [Fact]
    public async Task A_link_windows_cannot_open_is_a_failure_with_its_exit_code()
    {
        var sessions = new FakeSessions(Ada) { Result = new ProcessResult(5, "") };

        var result = await Executor(sessions, new Registered(Ada)).RunAsync(new JobContext("job-8", Ada),
            new LauncherPackageDefinition("steam", "730"), new Progress(), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("Windows could not open Steam (exit code 5).", result.Detail);
    }

    [Fact]
    public void The_real_registry_answers_for_a_scheme_windows_itself_registers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // Every Windows registers ms-settings for everyone, and nobody registers this one.
        var account = Environment.UserDomainName + "\\" + Environment.UserName;
        var registry = new WindowsProtocolRegistry();

        Assert.True(registry.IsRegistered("ms-settings", account));
        Assert.False(registry.IsRegistered("app-portal-test-" + Guid.NewGuid().ToString("N")[..8], account));
        Assert.False(registry.IsRegistered(@"steam\..\..", account));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LauncherHandoffExecutor Executor(FakeSessions sessions, IProtocolRegistry protocols)
        => new(sessions, protocols, NullLogger<LauncherHandoffExecutor>.Instance, _root);

    /// <summary>Knows every scheme for the accounts it was given, and remembers what it was asked.</summary>
    private sealed class Registered(params string[] accounts) : IProtocolRegistry
    {
        public List<(string Scheme, string Account)> Asked { get; } = [];

        public bool IsRegistered(string scheme, string account)
        {
            Asked.Add((scheme, account));
            return accounts.Contains(account, StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class Progress : IProgress<(int percent, string detail)>
    {
        public void Report((int percent, string detail) value)
        {
        }
    }
}
