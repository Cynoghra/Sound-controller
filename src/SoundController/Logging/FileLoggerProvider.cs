using System.IO;
using Microsoft.Extensions.Logging;

namespace SoundController.Logging;

/// <summary>
/// Minimal rolling file logger writing to
/// %LOCALAPPDATA%\SoundController\logs\soundcontroller-yyyyMMdd.log.
/// Deliberately dependency-free: a full logging framework is not worth the
/// size for a tray utility, but logs are essential for diagnosing unofficial
/// API breakage after GG updates.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    // Older logs are useful for "what happened when I plugged the controller
    // in yesterday"; more than a handful is just disk noise.
    private const int MaxLogFiles = 5;

    private readonly string _logDirectory;
    private readonly object _sync = new();
    private DateTime _lastPruneDateUtc = DateTime.MinValue;

    public FileLoggerProvider(string logDirectory)
    {
        _logDirectory = logDirectory;
        try
        {
            Directory.CreateDirectory(_logDirectory);
        }
        catch (IOException)
        {
            // Logging must never crash the app; Write() will retry creation.
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(LogLevel logLevel, string category, string message, Exception? exception)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(_logDirectory);
                string filePath = Path.Combine(_logDirectory, $"soundcontroller-{DateTime.Now:yyyyMMdd}.log");

                string line = string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}: {3}",
                    DateTime.Now, logLevel, category, message);
                if (exception is not null)
                {
                    line += Environment.NewLine + exception;
                }

                File.AppendAllText(filePath, line + Environment.NewLine);
                PruneOldLogsIfNeeded();
            }
        }
        catch (IOException)
        {
            // Disk full, file locked, etc. Swallow: a logging failure must
            // never take the tray application down.
        }
    }

    private void PruneOldLogsIfNeeded()
    {
        // Prune at most once per day per process; listing files on every log
        // line would be wasteful.
        var today = DateTime.UtcNow.Date;
        if (_lastPruneDateUtc == today)
        {
            return;
        }

        _lastPruneDateUtc = today;

        var files = Directory.GetFiles(_logDirectory, "soundcontroller-*.log")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.Name)
            .ToList();

        foreach (var file in files.Skip(MaxLogFiles))
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // In use or locked; it will be cleaned up on a later day.
            }
        }
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            owner.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
