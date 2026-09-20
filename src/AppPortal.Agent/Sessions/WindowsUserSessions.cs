using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

using AppPortal.Agent.Jobs;

namespace AppPortal.Agent.Sessions;

/// <summary>
/// The Win32 side of running something as the signed-in person. Every call into advapi32, userenv and
/// wtsapi32 in this project is in this one file, so the rest of the agent stays testable and there is
/// one place to look when Windows behaves differently from the documentation.
///
/// It deliberately does not elevate. The token a session hands back under User Account Control is the
/// limited one, and that is correct here: an installer that writes to a user profile needs no more, and
/// one that demands administrator rights is a packaging mistake the detail should name rather than a
/// thing to work around from a service.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsUserSessions(ILogger<WindowsUserSessions> logger) : IUserSessionLauncher
{
    public IReadOnlyList<string> SignedInAccounts()
    {
        var accounts = new List<string>();
        foreach (var (_, account) in Sessions())
        {
            accounts.Add(account);
        }

        return accounts;
    }

    public async Task<ProcessResult?> RunAsAsync(string account, string file, string arguments, Action<string>? onLine,
        TimeSpan timeout, CancellationToken ct)
    {
        var session = Sessions().FirstOrDefault(s => string.Equals(s.Account, account, StringComparison.OrdinalIgnoreCase));
        if (session.Account is null)
        {
            return null;
        }

        if (!WTSQueryUserToken(session.Id, out var token))
        {
            logger.LogWarning("Could not take the token for session {Session} ({Error})", session.Id, Marshal.GetLastWin32Error());
            return null;
        }

        using (token)
        {
            // The token a query hands back cannot start a process as it stands; it has to be duplicated
            // into a primary token first.
            if (!DuplicateTokenEx(token, MaximumAllowed, IntPtr.Zero, SecurityImpersonation, TokenPrimary, out var primary))
            {
                logger.LogWarning("Could not duplicate the session token ({Error})", Marshal.GetLastWin32Error());
                return null;
            }

            using (primary)
            {
                return await StartAsync(primary, file, arguments, onLine, timeout, ct);
            }
        }
    }

    private async Task<ProcessResult?> StartAsync(SafeTokenHandle token, string file, string arguments,
        Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
    {
        // Without the person's own environment block the process inherits SYSTEM's, and every installer
        // that writes to %LOCALAPPDATA% would put its files back in the wrong profile.
        if (!CreateEnvironmentBlock(out var environment, token, inherit: false))
        {
            logger.LogWarning("Could not build the environment for the session ({Error})", Marshal.GetLastWin32Error());
            environment = IntPtr.Zero;
        }

        // The process runs on the person's desktop, but what it prints still has to reach the job log:
        // an install that failed on a device nobody can reach is diagnosed from that and nothing else.
        // The write end is inheritable so the child gets it; the read end is not, or the child would
        // hold a copy open and the read below would never see the end of the stream.
        var security = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
        if (!CreatePipe(out var readEnd, out var writeEnd, ref security, 0))
        {
            logger.LogWarning("Could not make a pipe for the session process ({Error})", Marshal.GetLastWin32Error());
            readEnd = writeEnd = IntPtr.Zero;
        }
        else if (!SetHandleInformation(readEnd, HandleFlagInherit, 0))
        {
            logger.LogWarning("Could not detach the read end of the pipe ({Error})", Marshal.GetLastWin32Error());
        }

        var captured = writeEnd != IntPtr.Zero;
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
        if (captured)
        {
            startup.dwFlags = StartfUseStdHandles;
            startup.hStdOutput = writeEnd;
            startup.hStdError = writeEnd;
        }

        var command = new StringBuilder($"\"{file}\" {arguments}");
        try
        {
            if (!CreateProcessAsUser(token, null, command, IntPtr.Zero, IntPtr.Zero, captured,
                    CreateUnicodeEnvironment | CreateNoWindow, environment, null, ref startup, out var information))
            {
                logger.LogWarning("Could not start {File} in the session ({Error})", Path.GetFileName(file), Marshal.GetLastWin32Error());
                return null;
            }

            CloseHandle(information.hThread);
            using var process = System.Diagnostics.Process.GetProcessById(information.dwProcessId);
            CloseHandle(information.hProcess);

            // This side's copy of the write end goes now. While it is open the pipe never reaches its
            // end, and the read below would wait for a process that has already exited.
            if (captured)
            {
                CloseHandle(writeEnd);
                writeEnd = IntPtr.Zero;
            }

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            var output = captured
                ? ReadAsync(readEnd, onLine, deadline.Token)
                : Task.FromResult("");
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                return new ProcessResult(process.ExitCode, await output);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Kill(process);
                throw new TimeoutException($"{Path.GetFileName(file)} did not finish within {timeout.TotalMinutes:0} minutes.");
            }
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (writeEnd != IntPtr.Zero)
            {
                CloseHandle(writeEnd);
            }

            if (readEnd != IntPtr.Zero)
            {
                CloseHandle(readEnd);
            }
        }
    }

    /// <summary>
    /// Everything the child printed, broken on either terminator like the service-side runner, so an
    /// installer that draws a progress bar is still readable afterwards.
    /// </summary>
    private static async Task<string> ReadAsync(IntPtr readEnd, Action<string>? onLine, CancellationToken ct)
    {
        var everything = new StringBuilder();
        var line = new StringBuilder();
        try
        {
            await using var stream = new FileStream(new Microsoft.Win32.SafeHandles.SafeFileHandle(readEnd, ownsHandle: false),
                FileAccess.Read, 4096, isAsync: false);
            using var reader = new StreamReader(stream);
            var buffer = new char[1024];
            int read;
            while ((read = await reader.ReadAsync(buffer, ct)) != 0)
            {
                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] is '\r' or '\n')
                    {
                        if (line.Length > 0)
                        {
                            var text = line.ToString();
                            line.Clear();
                            everything.AppendLine(text);
                            onLine?.Invoke(text);
                        }

                        continue;
                    }

                    line.Append(buffer[i]);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Losing the transcript must not lose the install; the exit code is what decides it.
        }

        if (line.Length > 0)
        {
            everything.AppendLine(line.ToString());
            onLine?.Invoke(line.ToString());
        }

        return everything.ToString();
    }

    private static void Kill(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or SystemException)
        {
        }
    }

    /// <summary>Every interactive session with somebody signed in, as session id and DOMAIN\user.</summary>
    private IEnumerable<(int Id, string Account)> Sessions()
    {
        var found = new List<(int, string)>();
        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var list, out var count) == 0)
        {
            logger.LogWarning("Could not list the sessions ({Error})", Marshal.GetLastWin32Error());
            return found;
        }

        try
        {
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(list + (i * size));
                // Active is somebody at the machine; Disconnected is somebody still signed in whose
                // session is not on screen, which is a perfectly good place to install for them.
                if (info.State is not (WtsActive or WtsDisconnected))
                {
                    continue;
                }

                var user = Query(info.SessionId, WtsUserName);
                if (string.IsNullOrEmpty(user))
                {
                    continue;
                }

                var domain = Query(info.SessionId, WtsDomainName);
                found.Add((info.SessionId, string.IsNullOrEmpty(domain) ? user : $"{domain}\\{user}"));
            }
        }
        finally
        {
            WTSFreeMemory(list);
        }

        return found;
    }

    private static string? Query(int session, int what)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, what, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private const int WtsActive = 0;
    private const int WtsDisconnected = 4;
    private const int WtsUserName = 5;
    private const int WtsDomainName = 7;
    private const int MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const int HandleFlagInherit = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string pWinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    private sealed class SafeTokenHandle() : SafeHandle(IntPtr.Zero, ownsHandle: true)
    {
        public override bool IsInvalid => handle == IntPtr.Zero;

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern int WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int what, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(int session, out SafeTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateTokenEx(SafeTokenHandle existing, int access, IntPtr attributes,
        int level, int type, out SafeTokenHandle duplicate);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(SafeTokenHandle token, string? application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags, IntPtr environment, string? directory, ref STARTUPINFO startup, out PROCESS_INFORMATION information);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out IntPtr readEnd, out IntPtr writeEnd, ref SECURITY_ATTRIBUTES attributes, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, int mask, int flags);
}
