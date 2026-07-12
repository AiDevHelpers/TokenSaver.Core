using System;
using System.Collections.Generic;

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

    /// <summary>Logs an exception.</summary>
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

    public void LogInfo(string message) => Add(LogLevel.Info, message);
    public void LogError(string message) => Add(LogLevel.Error, message);
    public void LogException(Exception ex) => Add(LogLevel.Error, $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
    public void LogDebug(string message) => Add(LogLevel.Debug, message);

    private void Add(LogLevel level, string message)
    {
        _entries.Add(new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Message = message,
        });
    }

    public IReadOnlyList<LogEntry> Entries => _entries.AsReadOnly();

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
/// Static facade for logging operations during server lifecycle management.
/// </summary>
public static class ExtensionLoggerFacade
{
    private static IExtensionLogger _logger = new NullExtensionLogger();

    public static void SetLogger(IExtensionLogger logger) => _logger = logger ?? new NullExtensionLogger();

    public static void LogInfo(string message) => _logger.LogInfo(message);
    public static void LogError(string message) => _logger.LogError(message);
    public static void LogException(Exception ex) => _logger.LogException(ex);
    public static void LogDebug(string message) => _logger.LogDebug(message);
}
