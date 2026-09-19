using System.Collections.Concurrent;

namespace AppPortal.Server.Action1;

/// <summary>
/// In-memory stand-in for Action1. Every deployment advances one step each time its status is read:
/// Pending, then Running, then Success, after which the package appears in the installed inventory.
/// Used by the Development environment and by the test suite.
/// </summary>
public sealed class FakeAction1Client : IAction1Client
{
    private readonly ConcurrentDictionary<string, FakeDeployment> _deployments = new();
    private readonly ConcurrentDictionary<string, List<Action1InstalledSoftware>> _installed = new();

    public Task<Action1Endpoint?> GetEndpointAsync(string endpointId, CancellationToken ct)
        => Task.FromResult<Action1Endpoint?>(new Action1Endpoint(endpointId, $"FAKE-{endpointId[..Math.Min(8, endpointId.Length)].ToUpperInvariant()}", "Connected", DateTimeOffset.UtcNow.AddMinutes(-2)));

    public Task<IReadOnlyList<Action1InstalledSoftware>> GetInstalledSoftwareAsync(string endpointId, CancellationToken ct)
    {
        var list = _installed.GetOrAdd(endpointId, _ =>
        [
            new Action1InstalledSoftware("Microsoft Edge", "Microsoft Corporation", "128.0.2739.42"),
            new Action1InstalledSoftware("NVIDIA App", "NVIDIA Corporation", "11.0.2.341"),
        ]);
        lock (list)
        {
            return Task.FromResult<IReadOnlyList<Action1InstalledSoftware>>(list.ToList());
        }
    }

    public Task<Action1PackageVersion?> ResolvePackageVersionAsync(string packageId, string requestedVersion, CancellationToken ct)
    {
        if (packageId.Contains("missing", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<Action1PackageVersion?>(null);
        }

        var version = string.Equals(requestedVersion, "latest", StringComparison.OrdinalIgnoreCase) ? "1.0.0" : requestedVersion;
        return Task.FromResult<Action1PackageVersion?>(new Action1PackageVersion($"{packageId}:{version}", version));
    }

    public Task<string> StartDeploymentAsync(string endpointId, string automationName, string packageId, string version, string displaySummary, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        _deployments[id] = new FakeDeployment(endpointId, displaySummary, version);
        return Task.FromResult(id);
    }

    public Task<Action1DeploymentStatus> GetDeploymentStatusAsync(string automationId, string endpointId, CancellationToken ct)
    {
        if (!_deployments.TryGetValue(automationId, out var deployment))
        {
            return Task.FromResult(new Action1DeploymentStatus("Error", 0, "Unknown automation."));
        }

        var step = Interlocked.Increment(ref deployment.Step);
        var status = step switch
        {
            1 => new Action1DeploymentStatus("Pending", 0, "Waiting for the agent to pick up the job."),
            2 => new Action1DeploymentStatus("Running", 55, "Downloading and installing the package."),
            _ => new Action1DeploymentStatus("Success", 100, "The packages have been installed successfully."),
        };

        if (status.Status == "Success" && !deployment.Recorded)
        {
            deployment.Recorded = true;
            var list = _installed.GetOrAdd(endpointId, _ => []);
            lock (list)
            {
                list.Add(new Action1InstalledSoftware(deployment.DisplaySummary, "Fake Vendor", deployment.Version));
            }
        }

        return Task.FromResult(status);
    }

    public Task<IReadOnlyList<Action1Package>> SearchPackagesAsync(string nameFilter, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Action1Package>>(
        [
            new Action1Package("Google_Google_Chrome_1570243626751_builtin", "Google Chrome", "Google LLC", true),
            new Action1Package("Igor_Pavlov_7_Zip_1570578162075_builtin", "7-Zip", "Igor Pavlov", true),
        ]);

    private sealed class FakeDeployment(string endpointId, string displaySummary, string version)
    {
        public string EndpointId { get; } = endpointId;
        public string DisplaySummary { get; } = displaySummary;
        public string Version { get; } = version;
        public int Step;
        public bool Recorded;
    }
}
