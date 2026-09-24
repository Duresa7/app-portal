using AppPortal.Agent.Jobs;

namespace AppPortal.Agent.Sessions;

/// <summary>
/// Running something as the person sitting at the PC, from a service that is not them. The agent runs
/// as SYSTEM, and a great many Windows installers write into a user profile; started as SYSTEM those
/// land in the service account's profile, where the person who asked will never find them.
/// </summary>
public interface IUserSessionLauncher
{
    /// <summary>Every account with an interactive session on this device right now.</summary>
    IReadOnlyList<string> SignedInAccounts();

    /// <summary>
    /// Runs a command as <paramref name="account"/>, in that account's own session, and waits for it.
    /// Returns null when the account is not signed in, which is a thing to wait for rather than fail on.
    /// </summary>
    Task<ProcessResult?> RunAsAsync(string account, string file, string arguments, Action<string>? onLine,
        TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// The same, with <paramref name="pathFirst"/> ahead of PATH in that account's environment for this
    /// one process. The default ignores them, which is all a test double needs.
    /// </summary>
    Task<ProcessResult?> RunAsAsync(string account, string file, string arguments, IReadOnlyList<string> pathFirst,
        Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        => RunAsAsync(account, file, arguments, onLine, timeout, ct);
}

/// <summary>
/// What the agent uses away from Windows, and in tests. It reports nobody signed in, which makes every
/// per-user install park rather than run somewhere it should not.
/// </summary>
public sealed class NoUserSessions : IUserSessionLauncher
{
    public IReadOnlyList<string> SignedInAccounts() => [];

    public Task<ProcessResult?> RunAsAsync(string account, string file, string arguments, Action<string>? onLine,
        TimeSpan timeout, CancellationToken ct) => Task.FromResult<ProcessResult?>(null);
}
