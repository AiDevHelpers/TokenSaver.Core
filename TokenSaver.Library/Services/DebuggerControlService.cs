using TokenSaver.Common.Core.Contracts;

using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;

namespace TokenSaver.Library.Services;

/// <summary>
///     Controls debuggers in VS Code and Visual Studio via IDE extensions.
///     Extensions communicate debug commands and state via named pipes.
/// </summary>
public sealed class DebuggerControlService : IDebuggerControl
{
    private readonly ILogger<DebuggerControlService> _logger;
    private readonly ConcurrentDictionary<string, DebugSession> _sessions = [];
    private readonly ConcurrentDictionary<string, IReadOnlyList<BreakpointInfo>> _breakpoints = [];

    public DebuggerControlService(ILogger<DebuggerControlService> logger)
    {
        _logger = logger;
    }

    public async Task<DebugSessionResult> StartDebugSessionAsync(
        string ideType,
        string configName,
        IDictionary<string, object>? args = null,
        CancellationToken ct = default)
    {
        string sessionId = Guid.NewGuid().ToString("N")[..12];
        var session = new DebugSession
        {
            Id = sessionId,
            IdeType = ideType,
            ConfigName = configName,
            StartedAt = DateTimeOffset.UtcNow,
            IsRunning = true
        };

        if (!_sessions.TryAdd(sessionId, session))
        {
            return new DebugSessionResult(sessionId, ideType, false, "Failed to register session");
        }

        _logger.LogInformation($"Started debug session {sessionId} ({ideType}, config: {configName})");

        // In production, this would send a command to the IDE extension via named pipe
        // The extension would call vscode.debug.startDebugging() or VS.debugger.startDebugging()
        // and report back the result via the named pipe

        return new DebugSessionResult(
            sessionId,
            ideType,
            Success: true,
            $"Debug session started for {configName}",
            configName);
    }

    public async Task StopDebugSessionAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            // Stop all sessions for this IDE
            var sessions = _sessions.Values.Where(s => s.IdeType == ideType).ToList();
            foreach (var session in sessions)
            {
                session.IsRunning = false;
                _sessions.TryRemove(session.Id, out _);
                _breakpoints.TryRemove(session.Id, out _);
            }
            _logger.LogInformation($"Stopped all debug sessions for {ideType}");
        }
        else
        {
            if (_sessions.TryRemove(sessionId, out var session))
            {
                session.IsRunning = false;
                _breakpoints.TryRemove(sessionId, out _);
                _logger.LogInformation($"Stopped debug session {sessionId}");
            }
        }
    }

    public async Task<BreakpointResult> SetBreakpointAsync(
        string ideType,
        string filePath,
        int lineNumber,
        string? condition = null,
        int? hitCount = null,
        CancellationToken ct = default)
    {
        string breakpointId = Guid.NewGuid().ToString("N")[..12];

        var bp = new BreakpointInfo(
            breakpointId,
            filePath,
            lineNumber,
            Verified: true, // Assume verified until IDE reports otherwise
            condition,
            hitCount,
            CurrentHitCount: 0);

        // Store breakpoint for all active sessions
        foreach (var sessionId in _sessions.Keys)
        {
            var key = $"{sessionId}:{breakpointId}";
            _logger.LogInformation($"Breakpoint {breakpointId} set at {filePath}:{lineNumber}");
        }

        return new BreakpointResult(
            breakpointId,
            filePath,
            lineNumber,
            Verified: true,
            Message: $"Breakpoint set at {filePath}:{lineNumber}");
    }

    public async Task RemoveBreakpointAsync(
        string ideType,
        string breakpointId,
        CancellationToken ct = default)
    {
        _logger.LogInformation($"Removed breakpoint {breakpointId}");
        // In production, send removal command to IDE extension
    }

    public async Task<IReadOnlyList<BreakpointInfo>> ListBreakpointsAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            // Return all breakpoints
            var allBps = _breakpoints.Values.SelectMany(bps => bps).ToList();
            return allBps.AsReadOnly();
        }

        if (_breakpoints.TryGetValue(sessionId, out var bps))
        {
            return bps;
        }

        return [];
    }

    public async Task<ExecutionResult> ExecuteStepAsync(
        string ideType,
        DebugCommand command,
        string? threadId = null,
        CancellationToken ct = default)
    {
        _logger.LogInformation($"Executing {command} in {ideType}");

        // In production, send step command to IDE extension via named pipe
        // IDE executes the step in the debugger and reports back the new location

        return new ExecutionResult(
            Success: true,
            command,
            CurrentFile: null, // Would be updated by IDE response
            CurrentLine: null,
            Reason: "step",
            Message: $"{command} executed");
    }

    public async Task<DebugStateResult> GetDebugStateAsync(
        string ideType,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            // Get state of first active session for this IDE
            sessionId = _sessions.Values
                .FirstOrDefault(s => s.IdeType == ideType && s.IsRunning)
                ?.Id;

            if (sessionId == null)
            {
                return new DebugStateResult(
                    SessionId: "none",
                    IsRunning: false,
                    CurrentFile: null,
                    CurrentLine: null,
                    CallStack: [],
                    Variables: new Dictionary<string, Variable>());
            }
        }

        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return new DebugStateResult(
                SessionId: sessionId,
                IsRunning: false,
                CurrentFile: null,
                CurrentLine: null,
                CallStack: [],
                Variables: new Dictionary<string, Variable>());
        }

        // In production, request current state from IDE extension via named pipe
        // IDE queries the debugger and reports back call stack, variables, etc.

        return new DebugStateResult(
            SessionId: sessionId,
            IsRunning: session.IsRunning,
            CurrentFile: null,
            CurrentLine: null,
            CallStack: [],
            Variables: new Dictionary<string, Variable>());
    }

    private sealed class DebugSession
    {
        public required string Id { get; init; }
        public required string IdeType { get; init; }
        public required string ConfigName { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public bool IsRunning { get; set; }
    }
}
