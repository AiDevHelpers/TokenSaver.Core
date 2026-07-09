using System.Collections.Concurrent;

namespace TokenSaver.CodeHelper.AutoWorkflow;

/// <summary>In-memory store for active workflow sessions (same pattern as PlanStore).</summary>
public sealed class WorkflowSessionStore
{
    private readonly ConcurrentDictionary<string, WorkflowSession> _sessions = new();

    public void Save(WorkflowSession session)
    {
        _sessions[session.SessionId] = session;
    }

    public WorkflowSession? Get(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out WorkflowSession? s) ? s : null;
    }

    public bool TryUpdate(string sessionId, WorkflowState newState)
    {
        if (!_sessions.TryGetValue(sessionId, out WorkflowSession? s))
        {
            return false;
        }

        s.State = newState;
        return true;
    }

    /// <summary>Returns all sessions, optionally filtering to those older than the given minutes.</summary>
    public IReadOnlyList<WorkflowSession> GetAll(int? olderThanMinutes = null)
    {
        DateTime cutoff = olderThanMinutes.HasValue
            ? DateTime.UtcNow.AddMinutes(-olderThanMinutes.Value)
            : DateTime.MinValue;
        return _sessions.Values
            .Where(s => s.CreatedAt <= cutoff || !olderThanMinutes.HasValue)
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
    }
}
