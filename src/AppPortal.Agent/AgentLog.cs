using System.Text;

namespace AppPortal.Agent;

/// <summary>
/// The agent's log file, rotated at five megabytes and kept three deep. A service nobody is watching
/// writes for months, so the cap is what stops it filling a disk. Writing is serialised and a failure
/// is swallowed: a log that cannot be written is not worth missing a heartbeat over.
/// </summary>
public sealed class AgentLog(string path, long maxBytes = 5_000_000) : ILoggerProvider
{
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                var text = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {line}{Environment.NewLine}";
                if (File.Exists(path) && new FileInfo(path).Length + Encoding.UTF8.GetByteCount(text) > maxBytes)
                {
                    for (var i = 2; i >= 1; i--)
                    {
                        if (File.Exists(path + "." + i))
                        {
                            File.Move(path + "." + i, path + "." + (i + 1), overwrite: true);
                        }
                    }

                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(path, text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A full disk must not stop the next heartbeat.
            }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(AgentLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                log.Write($"{logLevel} {category}: {formatter(state, exception)}");
            }
        }
    }
}
