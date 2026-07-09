namespace TokenSaver.CodeHelper.Models.Planning;

public record CodePlan
{
    public string PlanId { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string SessionId { get; init; } = "";
    public PlanTaskKind TaskKind { get; init; }
    public string OriginalPrompt { get; init; } = "";
    public IReadOnlyList<PlanStep> Steps { get; init; } = [];
    public double OverallConfidence { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>One-or-two-sentence human-readable summary of what the plan will do.</summary>
    public string? Summary { get; init; }
}
