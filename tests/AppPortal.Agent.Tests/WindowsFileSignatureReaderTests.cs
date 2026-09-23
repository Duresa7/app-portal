using AppPortal.Agent.Update;

namespace AppPortal.Agent.Tests;

/// <summary>
/// Against real files and the real WinVerifyTrust, because the reader's only job is to agree with
/// Windows. They pass trivially off Windows, where the reader answers Unsupported by design.
/// </summary>
public sealed class WindowsFileSignatureReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "app-portal-signature-tests", Guid.NewGuid().ToString("N"));

    public WindowsFileSignatureReaderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void The_runtime_itself_reads_as_signed_by_Microsoft()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // System.Private.CoreLib ships Authenticode-signed by Microsoft in every runtime install. The
        // .NET 10 runtime's certificate reads CN=.NET, O=Microsoft Corporation; the CN has changed
        // between runtimes before, the organization has not.
        var signature = new WindowsFileSignatureReader().Read(typeof(object).Assembly.Location);

        Assert.Equal(SignatureState.Valid, signature.State);
        Assert.False(string.IsNullOrWhiteSpace(signature.Publisher));
        Assert.Equal("Microsoft Corporation", signature.Organization);
    }

    [Fact]
    public void Random_bytes_read_as_unsigned()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_root, "AppPortal-9.9.9-x64.msi");
        File.WriteAllBytes(path, Random.Shared.GetItems<byte>(Enumerable.Range(0, 256).Select(b => (byte)b).ToArray(), 64 * 1024));

        var signature = new WindowsFileSignatureReader().Read(path);

        Assert.Equal(SignatureState.Unsigned, signature.State);
        Assert.Null(signature.Publisher);
    }

    [Fact]
    public void A_signed_file_with_one_byte_changed_reads_as_invalid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var bytes = File.ReadAllBytes(typeof(object).Assembly.Location);
        bytes[bytes.Length / 2] ^= 0xFF;
        var path = Path.Combine(_root, "AppPortal.Tampered.dll");
        File.WriteAllBytes(path, bytes);

        var signature = new WindowsFileSignatureReader().Read(path);

        Assert.Equal(SignatureState.Invalid, signature.State);
        Assert.False(string.IsNullOrWhiteSpace(signature.Detail));
    }

    [Fact]
    public void Off_Windows_nothing_is_read_and_the_answer_is_unsupported()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(SignatureState.Unsupported, new WindowsFileSignatureReader().Read(typeof(object).Assembly.Location).State);
    }

    [Fact]
    public void A_file_that_is_not_there_is_not_valid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var signature = new WindowsFileSignatureReader().Read(Path.Combine(_root, "missing.msi"));

        Assert.NotEqual(SignatureState.Valid, signature.State);
    }
}
