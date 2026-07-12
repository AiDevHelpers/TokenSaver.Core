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
