using AiDevTools.TokenSaver.CodeHelper.Models.Patch;

namespace AiDevTools.TokenSaver.CodeHelper.AutoWorkflow;

public record WorkflowResult(
    string SessionId,
    IReadOnlyList<PatchResult> AppliedPatches,
    IReadOnlyList<DeferredStep> DeferredSteps,
    CapabilityScore Assessment,
    string Summary
);
