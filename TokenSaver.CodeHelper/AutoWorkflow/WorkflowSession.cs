namespace AiDevTools.TokenSaver.CodeHelper.AutoWorkflow;

public enum WorkflowState
{
    /// <summary>Waiting for the calling AI to return a skeleton design.</summary>
    WaitingForSkeleton,

    /// <summary>Executing plan steps.</summary>
    InProgress,

    /// <summary>All steps resolved (some may be deferred).</summary>
    Complete,

    /// <summary>Abandoned or timed out.</summary>
    Cancelled
}

public class WorkflowSession
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string OriginalPrompt { get; init; } = "";
    public string? PlanId { get; set; }
    public string SkeletonRequestPrompt { get; init; } = "";
    public WorkflowState State { get; set; } = WorkflowState.WaitingForSkeleton;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Primary file extension detected from plan path hints (e.g. ".cs").</summary>
    public string Language { get; init; } = ".cs";

    public string TaskKind { get; init; } = "General";

    public CapabilityScore? Assessment { get; set; }
}
