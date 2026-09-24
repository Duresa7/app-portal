using AppPortal.Agent.Jobs;
using AppPortal.Agent.Sessions;

namespace AppPortal.Agent.Tests;

/// <summary>
/// Signed-in accounts without Windows. The real launcher is the one file in the agent that calls into
/// advapi32 and wtsapi32; everything above it is decided by what this returns, which is what keeps the
/// parking, the account matching and the two scopes testable on either operating system.
/// </summary>
public sealed class FakeSessions(params string[] signedIn) : IUserSessionLauncher
{
    /// <summary>The calls that were made, as account and command, in order.</summary>
    public List<(string Account, string File, string Arguments)> Started { get; } = [];

    public ProcessResult Result { get; set; } = new(0, "");

    /// <summary>The directories each call asked to have ahead of PATH, in order.</summary>
    public List<IReadOnlyList<string>> PathFirst { get; } = [];

    public IReadOnlyList<string> SignedInAccounts() => signedIn;

    public Task<ProcessResult?> RunAsAsync(string account, string file, string arguments, IReadOnlyList<string> pathFirst,
        Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        PathFirst.Add(pathFirst);
        return RunAsAsync(account, file, arguments, onLine, timeout, ct);
    }

    public Task<ProcessResult?> RunAsAsync(string account, string file, string arguments, Action<string>? onLine,
        TimeSpan timeout, CancellationToken ct)
    {
        // Windows matches account names without regard to case, and so does the real launcher.
        if (!signedIn.Contains(account, StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult<ProcessResult?>(null);
        }

        Started.Add((account, file, arguments));
        onLine?.Invoke($"running as {account}");
        return Task.FromResult<ProcessResult?>(Result);
    }
}
