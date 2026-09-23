using AppPortal.Agent.Update;

namespace AppPortal.Agent.Tests;

public sealed class UpdateSignaturePolicyTests
{
    private const string Msi = "AppPortal-0.9.0-x64.msi";

    // Built with the platform's separator, so the file name comes out right on the Linux leg too.
    private static readonly string AgentPath = Path.Combine(Path.GetTempPath(), "App Portal", "AppPortal.Agent.exe");
    private static readonly string MsiPath = Path.Combine(Path.GetTempPath(), "AppPortal", "updates", Msi);

    private static readonly FileSignature Foundation = new(SignatureState.Valid, "SignPath Foundation", "SignPath Foundation", null);
    private static readonly FileSignature Unsigned = new(SignatureState.Unsigned, null, null, null);
    private static readonly FileSignature TestSigned = new(SignatureState.Invalid, "App Portal test", null, "0x800B0109");
    private static readonly FileSignature Unsupported = new(SignatureState.Unsupported, null, null, null);

    public static TheoryData<FileSignature> NotValid => new() { Unsigned, TestSigned, Unsupported };

    [Theory]
    [MemberData(nameof(NotValid))]
    public void An_agent_that_is_not_validly_signed_accepts_an_unsigned_update(FileSignature running)
    {
        // Rule 1: 0.8.0 and every build before the first signed release, self-built fleets, and
        // rehearsal installs signed with a test certificate. The MSI already passed its SHA-256 check.
        Assert.Null(UpdateSignaturePolicy.Decide(running, Unsigned, Msi));
    }

    [Theory]
    [MemberData(nameof(NotValid))]
    public void An_agent_that_is_not_validly_signed_accepts_any_signed_update(FileSignature running)
    {
        Assert.Null(UpdateSignaturePolicy.Decide(running, Foundation, Msi));
        Assert.Null(UpdateSignaturePolicy.Decide(running, TestSigned, Msi));
    }

    [Fact]
    public void A_signed_agent_refuses_an_unsigned_update()
    {
        Assert.Equal(
            "AppPortal-0.9.0-x64.msi is not signed, and the App Portal on this PC is signed by SignPath Foundation. It was not installed.",
            UpdateSignaturePolicy.Decide(Foundation, Unsigned, Msi));
    }

    [Fact]
    public void A_signed_agent_refuses_an_update_whose_signature_does_not_verify()
    {
        var tampered = new FileSignature(SignatureState.Invalid, null, null, "the file does not match its signature");

        Assert.Equal(
            "AppPortal-0.9.0-x64.msi carries a signature that does not verify (the file does not match its signature). It was not installed.",
            UpdateSignaturePolicy.Decide(Foundation, tampered, Msi));
    }

    [Fact]
    public void A_signed_agent_refuses_a_test_signed_update()
    {
        Assert.Equal(
            "AppPortal-0.9.0-x64.msi carries a signature that does not verify (0x800B0109). It was not installed.",
            UpdateSignaturePolicy.Decide(Foundation, TestSigned, Msi));
    }

    [Fact]
    public void A_signed_agent_treats_an_update_it_cannot_read_as_unsigned()
    {
        Assert.Equal(
            "AppPortal-0.9.0-x64.msi is not signed, and the App Portal on this PC is signed by SignPath Foundation. It was not installed.",
            UpdateSignaturePolicy.Decide(Foundation, Unsupported, Msi));
    }

    [Fact]
    public void A_signed_agent_refuses_an_update_signed_by_somebody_else()
    {
        var other = new FileSignature(SignatureState.Valid, "Contoso Ltd", "Contoso Ltd", null);

        Assert.Equal(
            "AppPortal-0.9.0-x64.msi is signed by Contoso Ltd, not by SignPath Foundation, who signed the App Portal on this PC. It was not installed.",
            UpdateSignaturePolicy.Decide(Foundation, other, Msi));
    }

    [Fact]
    public void A_publisher_that_differs_only_in_its_organization_is_somebody_else()
    {
        var sameName = new FileSignature(SignatureState.Valid, "SignPath Foundation", "Someone Else", null);

        Assert.Equal(
            "AppPortal-0.9.0-x64.msi is signed by SignPath Foundation, not by SignPath Foundation, who signed the App Portal on this PC. It was not installed.",
            UpdateSignaturePolicy.Decide(Foundation, sameName, Msi));
    }

    [Fact]
    public void The_same_publisher_is_accepted_whatever_the_case()
    {
        var shouting = new FileSignature(SignatureState.Valid, "SIGNPATH FOUNDATION", "signpath foundation", null);

        Assert.Null(UpdateSignaturePolicy.Decide(Foundation, Foundation, Msi));
        Assert.Null(UpdateSignaturePolicy.Decide(Foundation, shouting, Msi));
    }

    [Fact]
    public void A_signature_whose_revocation_could_not_be_checked_still_counts_as_valid()
    {
        var offline = Foundation with { Detail = "revocation could not be checked" };

        Assert.Null(UpdateSignaturePolicy.Decide(Foundation, offline, Msi));
    }

    [Fact]
    public void Refusal_reads_the_running_agent_once_and_names_the_msi_by_file_name()
    {
        var reader = new FakeReader(new Dictionary<string, FileSignature> { [AgentPath] = Foundation, [MsiPath] = Unsigned });
        var policy = new UpdateSignaturePolicy(reader, AgentPath);

        var first = policy.Refusal(MsiPath);
        var second = policy.Refusal(MsiPath);

        Assert.Equal(
            "AppPortal-0.9.0-x64.msi is not signed, and the App Portal on this PC is signed by SignPath Foundation. It was not installed.",
            first);
        Assert.Equal(first, second);
        // The running agent cannot change under a running service, so it is read once.
        Assert.Equal(1, reader.Reads[AgentPath]);
        Assert.Equal(2, reader.Reads[MsiPath]);
    }

    [Fact]
    public void Describe_says_which_rule_applies_to_this_agent()
    {
        var signed = new UpdateSignaturePolicy(new FakeReader(new() { [AgentPath] = Foundation }), AgentPath);
        var unsigned = new UpdateSignaturePolicy(new FakeReader(new() { [AgentPath] = Unsigned }), AgentPath);

        Assert.Equal(
            "This agent is signed by SignPath Foundation and installs only updates signed by the same publisher.",
            signed.Describe());
        Assert.Equal("This agent is not signed, so updates are checked by their SHA-256 alone.", unsigned.Describe());
    }

    private sealed class FakeReader(Dictionary<string, FileSignature> signatures) : IFileSignatureReader
    {
        public Dictionary<string, int> Reads { get; } = [];

        public FileSignature Read(string path)
        {
            Reads[path] = Reads.GetValueOrDefault(path) + 1;
            return signatures[path];
        }
    }
}
