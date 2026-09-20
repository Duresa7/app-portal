using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AppPortal.Setup.Services;

/// <summary>What a command left behind: its exit code and everything it wrote.</summary>
public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>
/// Starting a process, behind an interface so the exit-code mapping around msiexec is under test rather
/// than only provable on a virtual machine. The same seam the agent's executors use, kept separate
/// because the installer cannot take a dependency on the service it installs.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string file, string arguments, TimeSpan timeout, CancellationToken ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string file, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            throw ct.IsCancellationRequested
                ? new OperationCanceledException(ct)
                // A timeout is an answer the caller can show somebody, not a stack trace to unwind.
                : new TimeoutException($"{Path.GetFileName(file)} did not finish within {timeout.TotalMinutes:0} minutes.");
        }

        return new ProcessResult(process.ExitCode, (await output) + (await errors));
    }

    private static void Stop(Process process)
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
            // It finished on its own between the check and the kill, or it is already gone.
        }
    }
}
