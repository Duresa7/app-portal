namespace AppPortal.Server.Action1;

public sealed record Action1Endpoint(string Id, string Name, string Status, DateTimeOffset? LastSeen);

public sealed record Action1InstalledSoftware(string Name, string Vendor, string Version);

public sealed record Action1PackageVersion(string Id, string Version);

public sealed record Action1Package(string Id, string Name, string Vendor, bool Builtin);

/// <summary>Status of one endpoint inside one automation instance.</summary>
public sealed record Action1DeploymentStatus(string Status, int PercentComplete, string? Detail);

public sealed class Action1Exception : Exception
{
    public Action1Exception(string message, Exception? inner = null) : base(message, inner) { }
}

public interface IAction1Client
{
    Task<Action1Endpoint?> GetEndpointAsync(string endpointId, CancellationToken ct);

    Task<IReadOnlyList<Action1InstalledSoftware>> GetInstalledSoftwareAsync(string endpointId, CancellationToken ct);

    /// <summary>Resolves "latest" or an explicit version string to a published package version.</summary>
    Task<Action1PackageVersion?> ResolvePackageVersionAsync(string packageId, string requestedVersion, CancellationToken ct);

    /// <summary>Starts a Deploy Software automation for one endpoint and returns the automation instance ID.</summary>
    Task<string> StartDeploymentAsync(string endpointId, string automationName, string packageId, string version, string displaySummary, CancellationToken ct);

    Task<Action1DeploymentStatus> GetDeploymentStatusAsync(string automationId, string endpointId, CancellationToken ct);

    /// <summary>Searches the Software Repository by name so catalog authors can find real package IDs.</summary>
    Task<IReadOnlyList<Action1Package>> SearchPackagesAsync(string nameFilter, CancellationToken ct);
}

internal static class Action1Time
{
    /// <summary>Action1 stamps times as YYYY-MM-DD_HH-mm-ss in UTC.</summary>
    public static DateTimeOffset? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParseExact(value, "yyyy-MM-dd_HH-mm-ss", null,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
    }
}
