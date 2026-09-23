namespace AppPortal.Agent.Update;

/// <summary>
/// Whether a downloaded MSI may run, judged by who signed it. The SHA-256 check proves the MSI is the
/// file the release lists; it cannot tell a real release from one somebody with write access to the
/// releases replaced along with its SHA256SUMS. A publisher they cannot sign as can.
/// </summary>
/// <remarks>
/// The trust anchor is the running agent's own signature, never a publisher written into the code (ADR
/// 0002). An agent that is not validly signed, which is every build before the first signed release, a
/// fleet built by its owner, and a test-signed rehearsal, accepts what the SHA-256 check accepted.
/// </remarks>
public sealed class UpdateSignaturePolicy(IFileSignatureReader reader, string runningAgentPath, ILogger<UpdateSignaturePolicy>? logger = null)
{
    // Read once: the file cannot change under a running service, because an upgrade restarts it.
    private readonly Lazy<FileSignature> _running = new(() => reader.Read(runningAgentPath));

    /// <summary>Null when the MSI may run; otherwise the sentence update.json carries.</summary>
    public string? Refusal(string msiPath)
    {
        var name = Path.GetFileName(msiPath);
        var running = _running.Value;
        if (running.State != SignatureState.Valid)
        {
            logger?.LogInformation("This agent is not signed, so {Msi} is checked by its SHA-256 alone", name);
            return null;
        }

        if (running.Detail is not null)
        {
            logger?.LogWarning("The signature of this agent is accepted, but {Detail}", running.Detail);
        }

        var candidate = reader.Read(msiPath);
        if (candidate.State == SignatureState.Valid && candidate.Detail is not null)
        {
            logger?.LogWarning("The signature of {Msi} is accepted, but {Detail}", name, candidate.Detail);
        }

        return Decide(running, candidate, name);
    }

    /// <summary>One line for <c>--check</c>.</summary>
    public string Describe()
    {
        var running = _running.Value;
        return running.State == SignatureState.Valid
            ? $"This agent is signed by {Name(running)} and installs only updates signed by the same publisher."
            : "This agent is not signed, so updates are checked by their SHA-256 alone.";
    }

    internal static string? Decide(FileSignature running, FileSignature candidate, string msiName)
    {
        if (running.State != SignatureState.Valid)
        {
            return null;
        }

        var publisher = Name(running);
        if (candidate.State == SignatureState.Invalid)
        {
            return $"{msiName} carries a signature that does not verify ({candidate.Detail ?? "no reason given"}). It was not installed.";
        }

        // Unsupported cannot happen on the PC that read the running agent as valid, and if it did the
        // MSI would be no better known than an unsigned one.
        if (candidate.State != SignatureState.Valid)
        {
            return $"{msiName} is not signed, and the App Portal on this PC is signed by {publisher}. It was not installed.";
        }

        if (!string.Equals(running.Publisher, candidate.Publisher, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(running.Organization, candidate.Organization, StringComparison.OrdinalIgnoreCase))
        {
            return $"{msiName} is signed by {Name(candidate)}, not by {publisher}, who signed the App Portal on this PC. It was not installed.";
        }

        return null;
    }

    private static string Name(FileSignature signature) => signature.Publisher ?? signature.Organization ?? "an unnamed publisher";
}
