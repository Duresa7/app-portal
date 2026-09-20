using AppPortal.Shared;

namespace AppPortal.Agent.Jobs;

public interface IPackageExecutor
{
    string Kind { get; }

    Task<ExecutionResult> RunAsync(PackageDefinition d, IProgress<(int percent, string detail)> p, CancellationToken ct);
}

public sealed class StubExecutor : IPackageExecutor
{
    public string Kind => "*";

    public Task<ExecutionResult> RunAsync(PackageDefinition d, IProgress<(int percent, string detail)> p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new ExecutionResult(false, "no executor"));
    }
}
