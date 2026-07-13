namespace TokenSaver.Common.Core.Contracts;

/// <summary>
///     Contract for capturing and retrieving debug output (stdout/stderr) from processes
///     and IDE launches. Allows calling AI to read stack traces, build errors, and
///     diagnostic information.
///     See docs/debug-output-capture-contract.md.
/// </summary>
public interface IDebugOutputCapture
{
    /// <summary>
    ///     Get captured output for a process or IDE instance.
    /// </summary>
    /// <param name="processId">Process ID to query</param>
    /// <param name="ideType">Optional IDE type (e.g., "VsCode", "VisualStudio2026") — if provided, queries IDE output instead</param>
    /// <param name="linesLimit">Maximum lines to return (default 1000)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Captured stdout/stderr with timestamps and severity levels</returns>
    Task<DebugOutput> GetCapturedOutputAsync(
        int? processId = null,
        string? ideType = null,
        int linesLimit = 1000,
        CancellationToken ct = default);

    /// <summary>
    ///     Clear captured output for a process.
    /// </summary>
    /// <param name="processId">Process ID to clear</param>
    /// <param name="ct">Cancellation token</param>
    Task ClearOutputAsync(int processId, CancellationToken ct = default);

    /// <summary>
    ///     Search captured output for a pattern (regex or substring).
    /// </summary>
    /// <param name="pattern">Regex pattern or substring to search for</param>
    /// <param name="processId">Optional: limit search to specific process</param>
    /// <param name="severity">Optional: filter by severity level</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Matching lines with context</returns>
    Task<IEnumerable<DebugOutputLine>> SearchOutputAsync(
        string pattern,
        int? processId = null,
        DebugSeverity? severity = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Start capturing output from a Process instance.
    /// </summary>
    /// <param name="process">Process to capture output from (must have stdout/stderr redirected)</param>
    void StartCapturing(System.Diagnostics.Process process);

    /// <summary>
    ///     Start capturing output from a launched IDE.
    /// </summary>
    /// <param name="ideType">IDE type identifier (e.g., "VsCode", "VisualStudio2026")</param>
    /// <param name="process">Process to capture output from</param>
    void StartCapturingIDE(string ideType, System.Diagnostics.Process process);
}

public sealed record DebugOutput(
    int? ProcessId,
    string? IdeType,
    DateTime CapturedAt,
    IReadOnlyList<DebugOutputLine> Lines,
    bool IsComplete);

public sealed record DebugOutputLine(
    int LineNumber,
    DateTime Timestamp,
    DebugSeverity Severity,
    string Stream, // "stdout" or "stderr"
    string Content);

public enum DebugSeverity
{
    Info,
    Warning,
    Error,
    Fatal
}

/// <summary>
/// Contract for structured debug event capture and querying.
/// Captures tool execution, exceptions, state changes, and performance metrics
/// for monitoring across multiple extension instances.
/// See docs/debugger-event-capture-contract.md.
/// </summary>
public interface IDebuggerEventCapture
{
    /// <summary>Record a tool execution event.</summary>
    /// <param name="toolName">Name of the tool executed</param>
    /// <param name="durationMs">Execution duration in milliseconds</param>
    /// <param name="clientName">Optional: name of calling extension</param>
    /// <param name="clientVersion">Optional: version of calling extension</param>
    /// <param name="modelHint">Optional: model used by caller</param>
    /// <param name="isError">Whether execution resulted in error</param>
    /// <param name="errorMessage">Optional: error message if isError=true</param>
    void RecordToolExecution(
        string toolName,
        long durationMs,
        string? clientName = null,
        string? clientVersion = null,
        string? modelHint = null,
        bool isError = false,
        string? errorMessage = null);

    /// <summary>Record an unhandled exception with stack trace and context.</summary>
    void RecordException(
        Exception exception,
        string? toolName = null,
        string? clientName = null,
        string? sessionId = null);

    /// <summary>Record a state transition (session start/stop, breakpoint events, etc.).</summary>
    void RecordStateChange(
        string stateChange,
        string? sessionId = null,
        string? clientName = null,
        Dictionary<string, object>? data = null);

    /// <summary>Record a performance metric (latency, token usage, context size, etc.).</summary>
    void RecordMetric(
        string metricName,
        double value,
        string? unit = null,
        string? toolName = null,
        string? clientName = null);

    /// <summary>Get all events in the buffer (latest N events).</summary>
    IReadOnlyList<DebuggerEvent> GetAllEvents(int? limitCount = null);

    /// <summary>Query events by multiple filter dimensions.</summary>
    /// <returns>Filtered and optionally limited event list</returns>
    IReadOnlyList<DebuggerEvent> QueryEvents(
        DebuggerEventType? eventType = null,
        DebugSeverity? severity = null,
        string? clientName = null,
        string? toolName = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int? limitCount = null);

    /// <summary>Get aggregated event statistics.</summary>
    DebuggerEventStats GetEventStats();

    /// <summary>Clear all events from the buffer.</summary>
    void Clear();
}

/// <summary>Structured debug event record.</summary>
public sealed record DebuggerEvent(
    Guid Id,
    DateTime Timestamp,
    DebuggerEventType EventType,
    DebugSeverity Severity,
    DebuggerCallerInfo CallerInfo,
    string? ToolName,
    TimeSpan? Duration,
    Dictionary<string, object> Data,
    string? StackTrace,
    string? SessionId);

/// <summary>Caller information for debug events.</summary>
public sealed record DebuggerCallerInfo(
    string? ClientName,
    string? ClientVersion,
    string? ModelHint,
    string? IdeType);

/// <summary>Event type classifier.</summary>
public enum DebuggerEventType
{
    ToolExecution,
    Exception,
    StateChange,
    Metric,
    SessionEvent,
    Other
}

/// <summary>Debug event statistics aggregation.</summary>
public sealed record DebuggerEventStats(
    int TotalEventCount,
    Dictionary<DebuggerEventType, int> EventCountByType,
    Dictionary<DebugSeverity, int> EventCountBySeverity,
    Dictionary<string, int> EventCountByClient,
    DateTime? OldestEventTime,
    DateTime? NewestEventTime);

/// <summary>
/// Contract for capturing and analyzing problems (errors, warnings, code issues)
/// for code analysis, debugging, and error tracking.
/// </summary>
public interface IDebuggerProblemCapture
{
    /// <summary>Record a code analysis problem (compilation error, warning, code smell, etc.).</summary>
    /// <param name="problemType">Type: CompilationError, RuntimeError, Warning, CodeSmell, PerformanceIssue, SecurityIssue, etc.</param>
    /// <param name="title">Short problem description</param>
    /// <param name="filePath">Optional: file where problem was found</param>
    /// <param name="lineNumber">Optional: line number of problem</param>
    /// <param name="columnNumber">Optional: column number of problem</param>
    /// <param name="details">Optional: detailed problem description</param>
    /// <param name="suggestion">Optional: suggested fix or resolution</param>
    /// <param name="toolName">Optional: name of tool that detected the problem</param>
    /// <param name="clientName">Optional: name of calling extension</param>
    /// <param name="ruleId">Optional: rule ID from linter/analyzer (e.g., CS0001, CA1000)</param>
    void RecordProblem(
        string problemType,
        string title,
        string? filePath = null,
        int? lineNumber = null,
        int? columnNumber = null,
        string? details = null,
        string? suggestion = null,
        string? toolName = null,
        string? clientName = null,
        string? ruleId = null);

    /// <summary>Get all problems (optionally filtered).</summary>
    IReadOnlyList<DebuggerProblem> GetAllProblems(int? limitCount = null);

    /// <summary>Query problems by multiple filter dimensions.</summary>
    IReadOnlyList<DebuggerProblem> QueryProblems(
        string? problemType = null,
        DebugSeverity? severity = null,
        string? filePath = null,
        string? toolName = null,
        string? clientName = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int? limitCount = null);

    /// <summary>Get problems grouped by file for code analysis.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<DebuggerProblem>> GetProblemsByFile(string? filterProblemType = null);

    /// <summary>Get problems grouped by severity for prioritization.</summary>
    IReadOnlyDictionary<DebugSeverity, IReadOnlyList<DebuggerProblem>> GetProblemsBySeverity(string? filterProblemType = null);

    /// <summary>Get problem statistics (count by type, severity, file).</summary>
    DebuggerProblemStats GetProblemStats();

    /// <summary>Clear all recorded problems.</summary>
    void Clear();
}

/// <summary>Represents a single problem (error, warning, code issue).</summary>
public sealed record DebuggerProblem(
    Guid Id,
    DateTime Timestamp,
    string ProblemType,
    DebugSeverity Severity,
    string Title,
    string? FilePath,
    int? LineNumber,
    int? ColumnNumber,
    string? Details,
    string? Suggestion,
    string? ToolName,
    string? ClientName,
    string? RuleId,
    bool IsResolved = false);

/// <summary>Problem statistics for analysis and reporting.</summary>
public sealed record DebuggerProblemStats(
    int TotalProblemCount,
    int ErrorCount,
    int WarningCount,
    Dictionary<string, int> ProblemCountByType,
    Dictionary<string, int> ProblemCountByFile,
    IReadOnlyList<string> AffectedFiles,
    DateTime? OldestProblemTime,
    DateTime? NewestProblemTime);
