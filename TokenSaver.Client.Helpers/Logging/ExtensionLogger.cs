using System;
using System.Collections.Generic;
using System.Threading;

namespace TokenSaver.Client.Helpers.Logging;

/// <summary>
/// Abstraction for logging in client extensions (VS Code, Visual Studio, etc).
/// Allows extensions to provide their own output channels.
/// </summary>
public interface IExtensionLogger
{
    /// <summary>Logs an informational message.</summary>
    void LogInfo(string message);

    /// <summary>Logs an error message.</summary>
    void LogError(string message);

    /// <summary>Logs an exception with full chain.</summary>
    void LogException(Exception ex);

    /// <summary>Logs debug information.</summary>
    void LogDebug(string message);
}

/// <summary>
/// Default in-memory logger for testing or when no extension-specific logger is provided.
/// </summary>
public sealed class DefaultExtensionLogger : IExtensionLogger
{
    private readonly List<LogEntry> _entries = new();
    private readonly object _lock = new();

    public void LogInfo(string message) => Add(LogLevel.Info, message);
    public void LogError(string message) => Add(LogLevel.Error, message);
    public void LogException(Exception ex) => Add(LogLevel.Error, FormatException(ex));
    public void LogDebug(string message) => Add(LogLevel.Debug, message);

    private void Add(LogLevel level, string message)
    {
        lock (_lock)
        {
            _entries.Add(new LogEntry
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message,
            });
        }
    }

    private static string FormatException(Exception ex)
    {
        var msg = $"{ex.GetType().Name}: {ex.Message}";
        if (ex.InnerException != null)
            msg += $"\n  InnerException: {FormatException(ex.InnerException)}";
        if (!string.IsNullOrEmpty(ex.StackTrace))
            msg += $"\n{ex.StackTrace}";
        return msg;
    }

    public IReadOnlyList<LogEntry> GetEntries()
    {
        lock (_lock)
        {
            return _entries.AsReadOnly();
        }
    }

    public sealed record LogEntry
    {
        public DateTime Timestamp { get; init; }
        public LogLevel Level { get; init; }
        public string Message { get; init; } = string.Empty;

        public override string ToString() =>
            $"{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:G}] {Message}";
    }
}

public enum LogLevel
{
    Debug,
    Info,
    Error,
}

/// <summary>
/// Null logger that discards all messages (useful for optional logging).
/// </summary>
public sealed class NullExtensionLogger : IExtensionLogger
{
    public void LogInfo(string message) { }
    public void LogError(string message) { }
    public void LogException(Exception ex) { }
    public void LogDebug(string message) { }
}

/// <summary>
/// Thread-safe static facade for logging operations during server lifecycle management.
/// </summary>
public static class ExtensionLoggerFacade
{
    private static IExtensionLogger _logger = new NullExtensionLogger();
    private static readonly object _lock = new();

    public static void SetLogger(IExtensionLogger logger)
    {
        lock (_lock)
        {
            _logger = logger ?? new NullExtensionLogger();
        }
    }

    public static void LogInfo(string message)
    {
        lock (_lock)
        {
            _logger.LogInfo(message);
        }
    }

    public static void LogError(string message)
    {
        lock (_lock)
        {
            _logger.LogError(message);
        }
    }

    public static void LogException(Exception ex)
    {
        lock (_lock)
        {
            _logger.LogException(ex);
        }
    }

    public static void LogDebug(string message)
    {
        lock (_lock)
        {
            _logger.LogDebug(message);
        }
    }
}
