using AppPortal.Shared;

namespace AppPortal.Agent.Jobs;

/// <summary>
/// What an executor knows about the job besides its package: which file to write its log to, and who
/// asked, which for a per-user install decides whose session the installer runs in.
/// </summary>
public sealed record JobContext(string JobId, string? Requester, string Kind = InstallKind.Install);

public interface IPackageExecutor
{
    string Kind { get; }

    Task<ExecutionResult> RunAsync(JobContext job, PackageDefinition d, IProgress<(int percent, string detail)> p, CancellationToken ct);

    /// <summary>
    /// Takes the same package off again. Separate from installing rather than a flag on it, because
    /// almost nothing is shared: no download, no hash, and a different command per installer type.
    /// </summary>
    Task<ExecutionResult> UninstallAsync(JobContext job, PackageDefinition d, IProgress<(int percent, string detail)> p, CancellationToken ct);
}

public sealed class StubExecutor : IPackageExecutor
{
    public string Kind => "*";

    public Task<ExecutionResult> RunAsync(JobContext job, PackageDefinition d, IProgress<(int percent, string detail)> p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ExecutionResult(false, "no executor"));
    }

    public Task<ExecutionResult> UninstallAsync(JobContext job, PackageDefinition d, IProgress<(int percent, string detail)> p, CancellationToken ct)
        => RunAsync(job, d, p, ct);
}
