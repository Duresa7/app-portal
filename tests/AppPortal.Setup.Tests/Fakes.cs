using System.Collections.Concurrent;
using System.Reflection;

using AppPortal.Setup.Services;

namespace AppPortal.Setup.Tests;

/// <summary>A server that answers whatever the test wants, and records what it was asked.</summary>
public sealed class FakeProbe : IEnrollmentProbe
{
    public ProbeResult Reach { get; set; } = ProbeResult.Good();

    public ProbeResult Key { get; set; } = ProbeResult.Good(SetupEngine.Agent);

    public string? DeviceName { get; set; } = "PC-01";

    public List<string> Asked { get; } = [];

    public Task<ProbeResult> ReachAsync(string serverUrl, CancellationToken ct)
    {
        Asked.Add("reach " + serverUrl);
        return Task.FromResult(Reach);
    }

    public Task<ProbeResult> CheckKeyAsync(string serverUrl, string enrollmentKey, CancellationToken ct)
    {
        Asked.Add("key " + enrollmentKey);
        return Task.FromResult(Key);
    }

    public Task<string?> DeviceNameAsync(string serverUrl, string deviceToken, CancellationToken ct)
    {
        Asked.Add("device " + deviceToken);
        return Task.FromResult(DeviceName);
    }
}

/// <summary>A process that never starts. The test says what it would have returned.</summary>
public sealed class FakeProcessRunner(int exitCode = 0) : IProcessRunner
{
    public int ExitCode { get; set; } = exitCode;

    public ConcurrentQueue<string> Commands { get; } = new();

    /// <summary>Runs just before the fake exits, which is where a test pretends the agent enrolled.</summary>
    public Action<string>? WhileRunning { get; set; }

    public Task<ProcessResult> RunAsync(string file, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Commands.Enqueue(file + " " + arguments);
        WhileRunning?.Invoke(arguments);
        return Task.FromResult(new ProcessResult(ExitCode, ""));
    }
}

/// <summary>A package the test wrote itself, or none at all.</summary>
public sealed class FakeMsiSource(bool present = true) : IMsiSource
{
    public bool Present { get; } = present;

    public string? Extracted { get; private set; }

    public string Extract(string directory)
    {
        Directory.CreateDirectory(directory);
        Extracted = Path.Combine(directory, "AppPortal.msi");
        File.WriteAllText(Extracted, "package");
        return Extracted;
    }
}

/// <summary>A clock the test moves, so a minute of polling costs nothing.</summary>
public sealed class FakeClock(DateTimeOffset start)
{
    public DateTimeOffset Now { get; private set; } = start;

    public DateTimeOffset Read() => Now;

    /// <summary>Stands in for the wait between polls, and is where the clock moves.</summary>
    public Task WaitAsync(TimeSpan span, CancellationToken ct)
    {
        Now += span;
        Stepped?.Invoke(Now);
        return Task.CompletedTask;
    }

    /// <summary>Raised on each poll, so a test can drop a file in partway through.</summary>
    public Action<DateTimeOffset>? Stepped { get; set; }
}

/// <summary>A folder that cleans itself up, standing in for %ProgramData%\AppPortal.</summary>
public sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "app-portal-setup-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Write(string name, string content) => File.WriteAllText(System.IO.Path.Combine(Path, name), content);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The test has finished; a stray folder under %TEMP% is not a failure.
        }
    }
}

/// <summary>
/// Collects progress on the thread that reported it. <see cref="Progress{T}"/> would post the callback
/// somewhere else and the assertions would race the reports they are about.
/// </summary>
public sealed class RecordingProgress<T>(List<T> into) : IProgress<T>
{
    public void Report(T value) => into.Add(value);
}

/// <summary>Where the test project keeps its stand-in for the MSI.</summary>
public static class TestPackage
{
    public const string ResourceName = "FakePackage.msi";

    public static Assembly Assembly => typeof(TestPackage).Assembly;
}
