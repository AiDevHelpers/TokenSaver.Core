namespace AiDevTools.TokenSaver.CodeHelper.AutoWorkflow;

public record DeferredStep(
    int StepNumber,
    string Description,
    string? FilePath,
    string AIHandoffPrompt
);
