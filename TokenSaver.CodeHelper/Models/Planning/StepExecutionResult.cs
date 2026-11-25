using AiDevTools.TokenSaver.CodeHelper.Models.Patch;

namespace AiDevTools.TokenSaver.CodeHelper.Models.Planning;

public record StepExecutionResult
{
    public PlanStep Step { get; init; } = null!;
    public PatchResult? Patch { get; init; }

    /// <summary>True when the step requires the calling AI to make a decision before proceeding.</summary>
    public bool NeedsAI { get; init; }

    /// <summary>Formatted prompt to send to the calling AI when NeedsAI is true.</summary>
    public string? AIPrompt { get; init; }

    public string? Error { get; init; }
}
