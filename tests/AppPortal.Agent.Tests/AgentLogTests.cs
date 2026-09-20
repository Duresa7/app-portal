namespace AppPortal.Agent.Tests;

public sealed class AgentLogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));

    public AgentLogTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Rotation_keeps_the_three_most_recent_archives()
    {
        var path = Path.Combine(_root, "agent.log");
        using var log = new AgentLog(path, 100);
        for (var i = 1; i <= 6; i++)
        {
            log.Write($"line {i} " + new string('x', 40));
        }

        Assert.Contains("line 6", File.ReadAllText(path));
        Assert.Contains("line 5", File.ReadAllText(path + ".1"));
        Assert.Contains("line 4", File.ReadAllText(path + ".2"));
        Assert.Contains("line 3", File.ReadAllText(path + ".3"));
        Assert.Equal(4, Directory.GetFiles(_root).Length);
    }

    [Fact]
    public void Default_limit_rotates_at_five_megabytes()
    {
        var path = Path.Combine(_root, "agent.log");
        File.WriteAllBytes(path, new byte[5_000_000]);
        using var log = new AgentLog(path);
        log.Write("next heartbeat");
        Assert.Equal(5_000_000, new FileInfo(path + ".1").Length);
        Assert.Contains("next heartbeat", File.ReadAllText(path));
    }

    [Fact]
    public void A_small_log_is_appended_without_rotation()
    {
        var path = Path.Combine(_root, "agent.log");
        using var log = new AgentLog(path);
        log.Write("first");
        log.Write("second");
        Assert.Equal(2, File.ReadAllLines(path).Length);
        Assert.False(File.Exists(path + ".1"));
    }

    [Fact]
    public void An_unwritable_path_does_not_stop_the_agent()
    {
        using var log = new AgentLog(_root);
        log.Write("heartbeat");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
