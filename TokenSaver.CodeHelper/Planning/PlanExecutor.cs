using TokenSaver.CodeHelper.Models.Patch;
using TokenSaver.CodeHelper.Models.Planning;
using TokenSaver.CodeHelper.Patching;

using Microsoft.Extensions.Logging;

using System.Runtime.CompilerServices;

namespace TokenSaver.CodeHelper.Planning;

/// <summary>
///     Executes steps of a <see cref="CodePlan" /> using the <see cref="PatchEngine" />.
///     Pauses on RequiresAIHelp steps and yields them back to the caller.
/// </summary>
public sealed partial class PlanExecutor
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Executing step {N}: {Desc}")]
    private static partial void LogExecutingStep(ILogger logger, int n, string desc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Step {N} requires AI help")]
    private static partial void LogStepRequiresAiHelp(ILogger logger, int n);

    private readonly ILogger<PlanExecutor> _logger;
    private readonly PatchEngine _patch;
    private readonly PlanStore _store;

    public PlanExecutor(PatchEngine patch, PlanStore store, ILogger<PlanExecutor> logger)
    {
        _patch = patch;
        _store = store;
        _logger = logger;
    }

    /// <summary>
    ///     Executes a single step. Returns the result and whether the calling AI needs to act.
    ///     If <paramref name="resolvedContent" /> is supplied, it replaces the step's NewText
    ///     (used when a previous AI handoff was resolved).
    /// </summary>
    public async Task<StepExecutionResult> ExecuteStepAsync(
        CodePlan plan, int stepNumber, string workspaceRoot,
        string? resolvedContent = null, CancellationToken ct = default)
    {
        PlanStep? step = plan.Steps.FirstOrDefault(s => s.StepNumber == stepNumber);
        if (step == null)
        {
            return new StepExecutionResult
            {
                Step = new PlanStep { StepNumber = stepNumber, Description = "Step not found" },
                Error = $"Step {stepNumber} does not exist in plan {plan.PlanId}."
            };
        }

        // AI handoff — unless the caller supplied resolved content
        if (step.RequiresAIHelp && resolvedContent == null)
        {
            LogStepRequiresAiHelp(_logger, stepNumber);
            return new StepExecutionResult
            {
                Step = step,
                NeedsAI = true,
                AIPrompt = step.AIHandoffPrompt ??
                           ($"Step {stepNumber} ({step.Description}) requires your input. " +
                           $"Please provide the exact change needed for {step.FilePath ?? "the file"}, " +
                           $"then call ExecutePlanStep again with resolvedContent.")
            };
        }

        // Resolve operation — inject AI-provided content if given
        PatchOperation? op = step.Operation;
        if (resolvedContent != null && op != null)
        {
            op = op switch
            {
                ReplaceOperation r => r with { NewText = resolvedContent },
                InsertOperation i => i with { Content = resolvedContent },
                RenameOperation n => n with { NewName = resolvedContent },
                _ => op
            };
        }

        if (op == null)
        {
            return new StepExecutionResult
            {
                Step = step,
                Error = $"Step {stepNumber} has no operation and no resolved content was provided."
            };
        }

        LogExecutingStep(_logger, stepNumber, step.Description);

        PatchResult result = await _patch.ApplyAsync(op, workspaceRoot, ct);

        // Update step status in store
        UpdateStepStatus(plan, stepNumber, result.Success ? PlanStepStatus.Completed : PlanStepStatus.Failed);

        return new StepExecutionResult
        {
            Step = step,
            Patch = result,
            NeedsAI = false,
            Error = result.Success ? null : result.Error
        };
    }

    /// <summary>
    ///     Runs all steps in order. Yields on RequiresAIHelp or failure.
    /// </summary>
    public async IAsyncEnumerable<StepExecutionResult> ExecutePlanAsync(
        CodePlan plan, string workspaceRoot,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (PlanStep step in plan.Steps)
        {
            ct.ThrowIfCancellationRequested();

            StepExecutionResult result = await ExecuteStepAsync(plan, step.StepNumber, workspaceRoot, ct: ct);
            yield return result;

            // Stop on AI handoff or failure
            if (result.NeedsAI || (result.Patch != null && !result.Patch.Success))
            {
                yield break;
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdateStepStatus(CodePlan plan, int stepNumber, PlanStepStatus status)
    {
        CodePlan updated = plan with
        {
            Steps = plan.Steps.Select(s =>
                s.StepNumber == stepNumber ? s with { Status = status } : s).ToArray()
        };
        _ = _store.TryUpdate(plan.PlanId, updated);
    }
}
