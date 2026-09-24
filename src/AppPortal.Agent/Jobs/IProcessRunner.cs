using System.Diagnostics;
using System.Text;

namespace AppPortal.Agent.Jobs;

/// <summary>What a command left behind: its exit code and everything it wrote, in order.</summary>
public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>
/// Starting a process, behind an interface so that an executor can be tested without one. Every
/// executor talks to the outside world through this, which is what keeps the exit-code mapping and the
/// output parsing under test rather than only provable on a virtual machine.
/// </summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct);

    /// <summary>
    /// The same, with <paramref name="pathFirst"/> ahead of PATH for this one process, which is how a
    /// program started from outside its package finds the framework DLLs the package would have given
    /// it. The default ignores them, which is all a test double needs; the runner that starts real
    /// processes does not.
    /// </summary>
    Task<ProcessResult> RunAsync(string file, string arguments, IReadOnlyList<string> pathFirst, Action<string>? onLine,
        TimeSpan timeout, CancellationToken ct)
        => RunAsync(file, arguments, onLine, timeout, ct);
}

public sealed class ProcessRunner : IProcessRunner
{
    public Task<ProcessResult> RunAsync(string file, string arguments, Action<string>? onLine, TimeSpan timeout, CancellationToken ct)
        => RunAsync(file, arguments, [], onLine, timeout, ct);

    public async Task<ProcessResult> RunAsync(string file, string arguments, IReadOnlyList<string> pathFirst, Action<string>? onLine,
        TimeSpan timeout, CancellationToken ct)
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

        if (pathFirst.Count > 0)
        {
            // The key is matched without case on Windows, so this finds Path however it is spelled.
            process.StartInfo.Environment.TryGetValue("PATH", out var path);
            process.StartInfo.Environment["PATH"] = SearchPath.Prepend(pathFirst, path);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var everything = new StringBuilder();
        process.Start();
        var reading = Task.WhenAll(
            PumpAsync(process.StandardOutput, everything, onLine, deadline.Token),
            PumpAsync(process.StandardError, everything, onLine, deadline.Token));

        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await reading;
        }
        catch (OperationCanceledException)
        {
            Stop(process);
            // A timeout is the caller's answer, not an exception to unwind through: the detail it builds
            // from the output so far is more use than a stack trace from inside a process wrapper.
            throw ct.IsCancellationRequested
                ? new OperationCanceledException(ct)
                : new TimeoutException($"{Path.GetFileName(file)} did not finish within {timeout.TotalMinutes:0} minutes.");
        }

        return new ProcessResult(process.ExitCode, everything.ToString());
    }

    /// <summary>
    /// Reads the stream a character at a time and breaks on either terminator. A command that draws a
    /// progress bar rewrites one line with a carriage return and never sends a newline until it is
    /// finished, so reading whole lines would report nothing at all until the work was over.
    /// </summary>
    private static async Task PumpAsync(StreamReader reader, StringBuilder everything, Action<string>? onLine, CancellationToken ct)
    {
        var line = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) != 0)
        {
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (c is '\r' or '\n')
                {
                    Flush(line, everything, onLine);
                    continue;
                }

                line.Append(c);
            }
        }

        Flush(line, everything, onLine);
    }

    private static void Flush(StringBuilder line, StringBuilder everything, Action<string>? onLine)
    {
        if (line.Length == 0)
        {
            return;
        }

        var text = line.ToString();
        line.Clear();
        lock (everything)
        {
            everything.AppendLine(text);
        }

        onLine?.Invoke(text);
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
