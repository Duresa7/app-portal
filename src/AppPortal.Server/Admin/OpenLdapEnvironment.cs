using System.Runtime.InteropServices;

namespace AppPortal.Server.Admin;

/// <summary>
/// OpenLDAP performs the bind on every platform but Windows, and it reads its trust anchors from the
/// <c>LDAPTLS_CACERT</c> environment variable through <c>getenv</c>. .NET keeps its own copy of the
/// environment and does not write through to the process, so a value set with
/// <see cref="Environment.SetEnvironmentVariable(string, string)"/> is invisible to the library that
/// needs it. This writes the real one.
/// </summary>
public static class OpenLdapEnvironment
{
    public const string CaCertVariable = "LDAPTLS_CACERT";

    // DllImport rather than the source-generated LibraryImport: that one requires unsafe code, and one
    // three-argument call into libc is not worth turning it on for the whole project.
    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetEnv(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value,
        int overwrite);

    /// <summary>
    /// Points OpenLDAP at the certificate file. Returns false when the platform has no such library,
    /// which is not a failure: Windows validates through the connection's own callback instead.
    /// </summary>
    public static bool PointAtCertificateFile(string path, ILogger logger)
    {
        Environment.SetEnvironmentVariable(CaCertVariable, path);

        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            if (SetEnv(CaCertVariable, path, 1) == 0)
            {
                logger.LogInformation("OpenLDAP will validate directory certificates against {Path}", path);
                return true;
            }

            logger.LogError("Could not set {Variable}; directory sign-in will fail to verify certificates", CaCertVariable);
        }
        catch (DllNotFoundException)
        {
            logger.LogWarning("No libc to set {Variable} through; set it in the environment instead", CaCertVariable);
        }
        catch (EntryPointNotFoundException)
        {
            logger.LogWarning("This platform's libc has no setenv; set {Variable} in the environment instead", CaCertVariable);
        }

        return false;
    }
}
