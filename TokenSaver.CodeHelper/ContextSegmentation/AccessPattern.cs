namespace TokenSaver.CodeHelper.ContextSegmentation;

/// <summary>
/// Records a single task session: which segments were loaded, how they were used.
/// Appended to .tokensaver/context/access-patterns.jsonl for learning.
/// </summary>
public class AccessPattern
{
    public string SessionId { get; set; } = null!;

    /// <summary>When this session occurred.</summary>
    public DateTime Timestamp { get; set; }

    /// <summary>Detected task type: "Code", "Debug", "Planning", etc.</summary>
    public string TaskType { get; set; } = null!;

    /// <summary>Segments loaded during this session.</summary>
    public IReadOnlyList<string> SegmentsLoaded { get; set; } = [];

    /// <summary>Segments actually referenced/used (via file reads, symbol lookups).</summary>
    public IReadOnlyList<string> SegmentsUsed { get; set; } = [];

    /// <summary>Segments the user modified.</summary>
    public IReadOnlyList<string> SegmentsModified { get; set; } = [];

    /// <summary>User's original prompt/task description.</summary>
    public string? TaskDescription { get; set; }

    /// <summary>Outcome: succeeded, failed, abandoned.</summary>
    public SessionOutcome Outcome { get; set; } = SessionOutcome.Succeeded;
}

public enum SessionOutcome
{
    Succeeded,
    Failed,
    Abandoned,
    Unknown
}
