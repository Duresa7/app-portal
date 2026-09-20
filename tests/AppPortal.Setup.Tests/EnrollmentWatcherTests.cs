using System.Text.Json;

using AppPortal.Setup.Services;

namespace AppPortal.Setup.Tests;

/// <summary>
/// The wait after msiexec. The MSI only leaves a key file behind; whether this PC actually became a
/// device is decided inside the agent afterwards, and these two files are the only evidence of it.
/// </summary>
public sealed class EnrollmentWatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Both_files_arriving_after_the_install_is_a_successful_enrollment()
    {
        using var folder = new TemporaryFolder();
        var clock = new FakeClock(Start);
        var watcher = new EnrollmentWatcher(folder.Path, clock.Read, clock.WaitAsync);
        // The agent writes them a few seconds in, as it would on a real PC.
        clock.Stepped = at =>
        {
            if (at >= Start + TimeSpan.FromSeconds(5))
            {
                Enrolled(folder, "apd_token", at);
            }
        };

        var outcome = await watcher.WaitAsync(Start, EnrollmentWatcher.Limit, null, CancellationToken.None);

        Assert.True(outcome.Enrolled);
        Assert.Equal("apd_token", outcome.DeviceToken);
        Assert.Equal("https://portal.example.internal", outcome.ServerUrl);
        Assert.Null(outcome.Problem);
    }

    [Fact]
    public async Task Files_left_by_an_earlier_install_do_not_count_as_this_one()
    {
        using var folder = new TemporaryFolder();
        // A PC that was already enrolled, then had the MSI run over it again: the token is there from
        // before, and so is a heartbeat. Only a heartbeat sent after this install proves anything.
        Enrolled(folder, "apd_from_last_time", Start - TimeSpan.FromDays(3));
        var clock = new FakeClock(Start);
        var watcher = new EnrollmentWatcher(folder.Path, clock.Read, clock.WaitAsync);

        var outcome = await watcher.WaitAsync(Start, TimeSpan.FromSeconds(10), null, CancellationToken.None);

        Assert.False(outcome.Enrolled);
        Assert.Contains("has not checked in since the install", outcome.Problem);
    }

    [Fact]
    public async Task A_PC_that_never_enrolls_gives_up_at_the_limit_and_says_which_half_failed()
    {
        using var folder = new TemporaryFolder();
        var clock = new FakeClock(Start);
        var watcher = new EnrollmentWatcher(folder.Path, clock.Read, clock.WaitAsync);

        var outcome = await watcher.WaitAsync(Start, EnrollmentWatcher.Limit, null, CancellationToken.None);

        Assert.False(outcome.Enrolled);
        Assert.Contains("did not enroll", outcome.Problem);
        // The whole minute passed on the fake clock and none of it in real time.
        Assert.Equal(Start + EnrollmentWatcher.Limit, clock.Now);
    }

    [Fact]
    public async Task A_token_without_a_heartbeat_is_reported_as_enrolled_but_quiet()
    {
        using var folder = new TemporaryFolder();
        folder.Write("client.json", """{"serverUrl":"https://portal.example.internal","deviceToken":"apd_token"}""");
        var clock = new FakeClock(Start);
        var watcher = new EnrollmentWatcher(folder.Path, clock.Read, clock.WaitAsync);

        var outcome = await watcher.WaitAsync(Start, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        Assert.False(outcome.Enrolled);
        Assert.Contains("enrolled", outcome.Problem);
        Assert.Contains("keep trying", outcome.Problem);
    }

    [Fact]
    public async Task A_failed_heartbeat_is_not_mistaken_for_a_successful_one()
    {
        using var folder = new TemporaryFolder();
        folder.Write("client.json", """{"serverUrl":"https://portal.example.internal","deviceToken":"apd_token"}""");
        folder.Write("agent.json", Status(Start + TimeSpan.FromSeconds(2), "Failed"));
        var clock = new FakeClock(Start);
        var watcher = new EnrollmentWatcher(folder.Path, clock.Read, clock.WaitAsync);

        var outcome = await watcher.WaitAsync(Start, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        Assert.False(outcome.Enrolled);
        Assert.Contains("last check-in failed", outcome.Problem);
    }

    [Fact]
    public async Task Half_written_files_are_waited_out_rather_than_treated_as_a_failure()
    {
        using var folder = new TemporaryFolder();
        folder.Write("client.json", "{\"serverUrl\":\"https://portal.exam");
        var clock = new FakeClock(Start);
        var watcher = new EnrollmentWatcher(folder.Path, clock.Read, clock.WaitAsync);
        clock.Stepped = at =>
        {
            if (at >= Start + TimeSpan.FromSeconds(3))
            {
                Enrolled(folder, "apd_token", at);
            }
        };

        var outcome = await watcher.WaitAsync(Start, EnrollmentWatcher.Limit, null, CancellationToken.None);

        Assert.True(outcome.Enrolled);
    }

    [Fact]
    public async Task Progress_is_reported_as_each_half_lands()
    {
        using var folder = new TemporaryFolder();
        var clock = new FakeClock(Start);
        var watcher = new EnrollmentWatcher(folder.Path, clock.Read, clock.WaitAsync);
        clock.Stepped = at =>
        {
            if (at == Start + TimeSpan.FromSeconds(2))
            {
                folder.Write("client.json", """{"serverUrl":"https://portal.example.internal","deviceToken":"apd_token"}""");
            }

            if (at >= Start + TimeSpan.FromSeconds(4))
            {
                folder.Write("agent.json", Status(at, "Succeeded"));
            }
        };
        var seen = new List<EnrollmentProgress>();

        await watcher.WaitAsync(Start, EnrollmentWatcher.Limit, new RecordingProgress<EnrollmentProgress>(seen), CancellationToken.None);

        Assert.Contains(seen, step => step is { Configured: false, HeartbeatSent: false });
        Assert.Contains(seen, step => step is { Configured: true, HeartbeatSent: false });
        Assert.Contains(seen, step => step is { Configured: true, HeartbeatSent: true });
    }

    private static void Enrolled(TemporaryFolder folder, string token, DateTimeOffset at)
    {
        folder.Write("client.json", $$"""{"serverUrl":"https://portal.example.internal","deviceToken":"{{token}}"}""");
        folder.Write("agent.json", Status(at, "Succeeded"));
    }

    private static string Status(DateTimeOffset at, string outcome)
        => JsonSerializer.Serialize(
            new { lastHeartbeatAt = at, outcome },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
