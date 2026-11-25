using AiDevTools.TokenSaver.CodeHelper.Models.Patch;

namespace AiDevTools.TokenSaver.CodeHelper.Models.Planning;

public enum PlanStepStatus
{
    Pending,
    InProgress,
    Completed,
    Failed,
    AIHandoff,
    Skipped
}

public record PlanStep
{
    public int StepNumber { get; init; }
    public string Description { get; init; } = "";
    public string? FilePath { get; init; }
    public PatchOperation? Operation { get; init; }

    /// <summary>0.0–1.0 model confidence in this step being correct.</summary>
    public double Confidence { get; init; }

    /// <summary>When true, executor pauses and returns this step to the calling AI.</summary>
    public bool RequiresAIHelp { get; init; }

    /// <summary>Prompt to send to the calling AI when RequiresAIHelp is true.</summary>
    public string? AIHandoffPrompt { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
    public PlanStepStatus Status { get; init; } = PlanStepStatus.Pending;
}
