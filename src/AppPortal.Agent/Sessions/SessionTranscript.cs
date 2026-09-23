using System.Text;

namespace AppPortal.Agent.Sessions;

/// <summary>
/// What a process started in somebody's session printed, and how long to wait for the rest of it once
/// the process has gone. Apart from the Win32 launcher so the rule can be tested without a session.
/// <para>
/// The rule exists because the end of the stream is not the end of the installer. The pipe's write end
/// is inherited, so an installer that starts its app when it finishes (every Squirrel installer does)
/// hands its copy on to that app, and the stream stays open for as long as the app runs. The exit code
/// decides the install; the transcript is only what explains it, and it is not worth holding the job
/// at Installing, and every job queued behind it, for as long as somebody keeps an app open.
/// </para>
/// </summary>
public sealed class SessionTranscript(Action<string>? onLine = null)
{
    /// <summary>
    /// How long to wait for the rest of the output after the process has exited. An installer that
    /// exited has printed what it will print; what arrives later comes from something it left running.
    /// </summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly StringBuilder _everything = new();
    private readonly StringBuilder _line = new();
    private string? _closed;

    /// <summary>
    /// Takes the next piece of output, broken on either terminator like the service-side runner, so an
    /// installer that draws a progress bar is still readable afterwards. Ignored once closed.
    /// </summary>
    public void Append(ReadOnlySpan<char> chars)
    {
        lock (_gate)
        {
            if (_closed is not null)
            {
                return;
            }

            foreach (var c in chars)
            {
                if (c is '\r' or '\n')
                {
                    EndLine();
                    continue;
                }

                _line.Append(c);
            }
        }
    }

    /// <summary>
    /// Everything read so far, an unfinished last line included. Nothing is taken after this, so a
    /// read that returns late cannot add a line to a job that has already been reported.
    /// </summary>
    public string Close()
    {
        lock (_gate)
        {
            if (_closed is null)
            {
                EndLine();
                _closed = _everything.ToString();
            }

            return _closed;
        }
    }

    /// <summary>
    /// Called once the process has exited. Waits for <paramref name="reading"/> to reach the end of the
    /// stream for at most <paramref name="grace"/>; after that it asks for the pending read to be
    /// cancelled, gives it the same time again to stop, and returns what was read either way.
    /// </summary>
    public async Task<string> AfterExitAsync(Task reading, Action cancelRead, TimeSpan grace)
    {
        if (!await EndsWithin(reading, grace))
        {
            cancelRead();
            await EndsWithin(reading, grace);
        }

        return Close();
    }

    private static async Task<bool> EndsWithin(Task reading, TimeSpan grace)
    {
        try
        {
            await reading.WaitAsync(grace);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception)
        {
            // A read that failed has ended. Losing the transcript must not lose the install; the exit
            // code is what decides it.
            return true;
        }
    }

    private void EndLine()
    {
        if (_line.Length == 0)
        {
            return;
        }

        var text = _line.ToString();
        _line.Clear();
        _everything.AppendLine(text);
        onLine?.Invoke(text);
    }
}
