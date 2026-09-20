namespace AppPortal.Agent.Jobs;

/// <summary>
/// One file per job under %ProgramData%\AppPortal\jobs. An install that fails on a device nobody can
/// reach is diagnosed from what the installer printed, and the detail that reaches the server is one
/// sentence, so the whole transcript has to live somewhere.
/// </summary>
public sealed class JobLog
{
    private readonly string? _path;

    public JobLog(string stateDirectory, string jobId)
    {
        try
        {
            var directory = Path.Combine(stateDirectory, "jobs");
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, Safe(jobId) + ".log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the log must not lose the install.
            _path = null;
        }
    }

    public void Write(string line)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(_path, $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A job id is a server-issued identifier, but it names a file, so it is checked like one.</summary>
    private static string Safe(string jobId)
    {
        var cleaned = new string(jobId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return cleaned.Length == 0 ? "job" : cleaned[..Math.Min(cleaned.Length, 64)];
    }
}
