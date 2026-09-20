using AppPortal.Shared;

namespace AppPortal.Agent.Jobs;

public interface IPackageExecutor
{
    string Kind { get; }

    // The job id names the log file the executor writes, which is where a failure on an unreachable
    // device is diagnosed from. The detail that reaches the server is one sentence.
    Task<ExecutionResult> RunAsync(string jobId, PackageDefinition d, IProgress<(int percent, string detail)> p, CancellationToken ct);
}

public sealed class StubExecutor : IPackageExecutor
{
    public string Kind => "*";

    public Task<ExecutionResult> RunAsync(string jobId, PackageDefinition d, IProgress<(int percent, string detail)> p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ExecutionResult(false, "no executor"));
    }
}
