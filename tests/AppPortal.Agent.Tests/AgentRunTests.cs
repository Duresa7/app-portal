using AppPortal.Shared;

namespace AppPortal.Agent.Tests;

/// <summary>
/// The whole host, started the way the service and CI start it. The worker has its own tests; what is
/// covered here is the part around it, where an exit code has to survive the host shutting down.
/// </summary>
[Collection("Agent settings")]
public sealed class AgentRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-tests", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _previous = new();

    public AgentRunTests()
    {
        Directory.CreateDirectory(_root);
        SetEnvironment("APPPORTAL_CONFIG", Path.Combine(_root, "client.json"));
        // A port nothing listens on, so the one heartbeat fails and the run reports a failure exit code.
        SetEnvironment("APPPORTAL_SERVER_URL", "http://127.0.0.1:1");
        SetEnvironment("APPPORTAL_DEVICE_TOKEN", "apd_test");
    }

    [Fact]
    public async Task A_single_run_reports_the_heartbeat_outcome_instead_of_throwing()
    {
        var code = await AgentRun.MainAsync(["--console", "--once"]);

        Assert.Equal(1, code);
        Assert.True(File.Exists(Path.Combine(_root, "agent.json")));
        Assert.True(File.Exists(Path.Combine(_root, "agent.log")));
    }

    [Fact]
    public async Task Asking_to_install_and_uninstall_at_once_is_refused()
    {
        Assert.Equal(2, await AgentRun.MainAsync(["--install", "--uninstall"]));
    }

    [Fact]
    public void State_lives_beside_the_settings_file_the_run_actually_reads()
    {
        Assert.Equal(Path.Combine(_root, "client.json"), PortalSettings.ResolvedPath);
    }

    private void SetEnvironment(string name, string? value)
    {
        _previous.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        Directory.Delete(_root, recursive: true);
    }
}
