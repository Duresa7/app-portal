using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace AppPortal.Agent.Update;

/// <summary>What an Authenticode check made of a file.</summary>
public enum SignatureState
{
    /// <summary>No signature, or a file type that cannot carry one.</summary>
    Unsigned,

    /// <summary>Signed, the file matches its signature, and the chain ends at a root this PC trusts.</summary>
    Valid,

    /// <summary>A signature is there and does not verify: tampered, revoked, expired, or an untrusted root.</summary>
    Invalid,

    /// <summary>Not Windows. Nothing was read.</summary>
    Unsupported,
}

// Publisher is the signer certificate's CN, Organization its O; both null unless a certificate was read.
public sealed record FileSignature(SignatureState State, string? Publisher, string? Organization, string? Detail);

/// <summary>
/// Reads a file's Authenticode signature. An interface so the update decision is tested with whatever
/// signature a test needs, on any OS, without a certificate.
/// </summary>
public interface IFileSignatureReader
{
    FileSignature Read(string path);
}

/// <summary>
/// Asks WinVerifyTrust, the same check Windows makes before it shows a publisher in a UAC prompt. It
/// reads MSI signatures as well as PE ones. Revocation soft-fails the way Windows does: when no CRL or
/// OCSP endpoint answers, the signature is still valid and <see cref="FileSignature.Detail"/> says so.
/// </summary>
public sealed class WindowsFileSignatureReader : IFileSignatureReader
{
    private const uint TrustNoSignature = 0x800B0100;
    private const uint TrustSubjectFormUnknown = 0x800B0003;
    private const uint TrustProviderUnknown = 0x800B0001;
    private const uint TrustBadDigest = 0x80096010;
    private const uint CryptRevoked = 0x80092010;
    private const uint CryptRevocationOffline = 0x80092013;
    private const uint CertRevocationFailure = 0x800B010E;

    public FileSignature Read(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new FileSignature(SignatureState.Unsupported, null, null, null);
        }

        try
        {
            var result = Verify(path, RevokeWholeChain);
            if (result.Code is CryptRevocationOffline or CertRevocationFailure)
            {
                var offline = Verify(path, RevokeNone);
                return offline.Code == 0
                    ? new FileSignature(SignatureState.Valid, offline.Publisher, offline.Organization, "revocation could not be checked")
                    : Map(offline);
            }

            return Map(result);
        }
        catch (Exception ex)
        {
            return new FileSignature(SignatureState.Invalid, null, null, ex.GetType().Name);
        }
    }

    private static FileSignature Map(Verification result) => result.Code switch
    {
        0 => new FileSignature(SignatureState.Valid, result.Publisher, result.Organization, null),
        TrustNoSignature or TrustSubjectFormUnknown or TrustProviderUnknown => new FileSignature(SignatureState.Unsigned, null, null, null),
        CryptRevoked => new FileSignature(SignatureState.Invalid, result.Publisher, result.Organization, "the signing certificate was revoked"),
        TrustBadDigest => new FileSignature(SignatureState.Invalid, result.Publisher, result.Organization, "the file does not match its signature"),
        _ => new FileSignature(SignatureState.Invalid, result.Publisher, result.Organization, $"0x{result.Code:X8}"),
    };

    [SupportedOSPlatform("windows")]
    private static Verification Verify(string path, uint revocation)
    {
        var action = GenericVerifyV2;
        var filePath = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = IntPtr.Zero;
        try
        {
            var file = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
                pcwszFilePath = filePath,
            };
            fileInfo = Marshal.AllocCoTaskMem(Marshal.SizeOf<WINTRUST_FILE_INFO>());
            Marshal.StructureToPtr(file, fileInfo, fDeleteOld: false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = UiNone,
                fdwRevocationChecks = revocation,
                dwUnionChoice = ChoiceFile,
                pFile = fileInfo,
                dwStateAction = StateActionVerify,
            };

            var code = unchecked((uint)WinVerifyTrust(NoInteractiveUser, ref action, ref data));
            try
            {
                var (publisher, organization) = Signer(data.hWVTStateData);
                return new Verification(code, publisher, organization);
            }
            finally
            {
                // The state data holds the parsed signature and chain until it is closed.
                data.dwStateAction = StateActionClose;
                WinVerifyTrust(NoInteractiveUser, ref action, ref data);
            }
        }
        finally
        {
            if (fileInfo != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfo);
            }

            Marshal.FreeCoTaskMem(filePath);
        }
    }

    /// <summary>
    /// The leaf certificate of the first signer, when WinVerifyTrust got far enough to find one. A file
    /// with no signature has none, and that is not an error.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static (string? Publisher, string? Organization) Signer(IntPtr state)
    {
        if (state == IntPtr.Zero)
        {
            return (null, null);
        }

        var provider = WTHelperProvDataFromStateData(state);
        if (provider == IntPtr.Zero)
        {
            return (null, null);
        }

        var signer = WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        if (signer == IntPtr.Zero)
        {
            return (null, null);
        }

        var certificate = WTHelperGetProvCertFromChain(signer, 0);
        if (certificate == IntPtr.Zero)
        {
            return (null, null);
        }

        // CRYPT_PROVIDER_CERT starts with a DWORD size and then the certificate context pointer, which
        // alignment puts one pointer's width in on both x86 and x64.
        var context = Marshal.ReadIntPtr(certificate, IntPtr.Size);
        if (context == IntPtr.Zero)
        {
            return (null, null);
        }

        // The constructor duplicates the context, so the copy outlives the state data it came from.
        using var x509 = new X509Certificate2(context);
        string? publisher = null;
        string? organization = null;
        foreach (var name in x509.SubjectName.EnumerateRelativeDistinguishedNames())
        {
            var value = name.GetSingleElementValue();
            switch (name.GetSingleElementType().Value)
            {
                case "2.5.4.3":
                    publisher ??= value;
                    break;
                case "2.5.4.10":
                    organization ??= value;
                    break;
            }
        }

        return (publisher, organization);
    }

    private readonly record struct Verification(uint Code, string? Publisher, string? Organization);

    private const uint UiNone = 2;
    private const uint RevokeNone = 0;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionVerify = 1;
    private const uint StateActionClose = 2;

    // INVALID_HANDLE_VALUE: there is no interactive user to show anything to, which as SYSTEM is true.
    private static readonly IntPtr NoInteractiveUser = new(-1);

    // WINTRUST_ACTION_GENERIC_VERIFY_V2: Authenticode, as Explorer and UAC check it.
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WINTRUST_DATA data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint certificate);
}
