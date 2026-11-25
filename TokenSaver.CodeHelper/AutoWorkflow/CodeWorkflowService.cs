using AiDevTools.TokenSaver.CodeHelper.Configuration;
using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.CodeHelper.Models.Patch;
using AiDevTools.TokenSaver.CodeHelper.Models.Planning;
using AiDevTools.TokenSaver.CodeHelper.Planning;
using AiDevTools.TokenSaver.CodeHelper.Research;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using System.Globalization;
using System.Text;

namespace AiDevTools.TokenSaver.CodeHelper.AutoWorkflow;

/// <summary>
///     Orchestrates the collaborative skeleton workflow:
///     1. Detect code-build intent in the prompt.
///     2. Build a plan and assess capability.
///     3. Ask the calling AI for a skeleton design.
///     4. Implement CanDo steps; hand off the rest back to the AI.
/// </summary>
public sealed partial class CodeWorkflowService
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow {Session}: capability={Level} score={Score:F2}")]
    private static partial void LogWorkflowCapability(ILogger logger, string session, CapabilityLevel level, double score);

    [LoggerMessage(Level = LogLevel.Information, Message = "Workflow {Session} complete: {Applied} applied, {Deferred} deferred.")]
    private static partial void LogWorkflowComplete(ILogger logger, string session, int applied, int deferred);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting auto-workflow for session {SessionId}")]
    private static partial void LogStartingAutoWorkflow(ILogger logger, string sessionId);

    private static readonly string[] _positiveSignals =
    [
        "create", "add", "implement", "build", "write", "generate", "scaffold",
        "new class", "new method", "new file", "new interface", "new service",
        "add feature", "add support", "add a", "add an"
    ];

    private static readonly string[] _negativeSignals =
    [
        "explain", "why does", "what is", "how does", "show me", "list", "describe",
        "what are", "tell me", "summarize", "what's wrong", "debug"
    ];

    private readonly CapabilityAssessor _assessor;
    private readonly CodeHelperConfig _cfg;
    private readonly PlanExecutor _executor;
    private readonly SymbolSearchService _index;
    private readonly ILogger<CodeWorkflowService> _log;
    private readonly PlanBuilder _planBuilder;
    private readonly PlanStore _planStore;
    private readonly WorkflowSessionStore _sessionStore;

    public CodeWorkflowService(
        CapabilityAssessor assessor,
        PlanBuilder planBuilder,
        PlanExecutor executor,
        PlanStore planStore,
        WorkflowSessionStore sessionStore,
        SymbolSearchService index,
        IOptions<CodeHelperConfig> config,
        ILogger<CodeWorkflowService> logger)
    {
        _assessor = assessor;
        _planBuilder = planBuilder;
        _executor = executor;
        _planStore = planStore;
        _sessionStore = sessionStore;
        _index = index;
        _cfg = config.Value;
        _log = logger;
    }

    // ── Intent detection ──────────────────────────────────────────────────────

    /// <summary>
    ///     Fast keyword scan — no model call.
    ///     Returns true when the prompt looks like a "please build/create code" request
    ///     rather than a question, explanation request, or debugging task.
    /// </summary>
    public bool DetectCodeBuildIntent(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return false;
        }

        // Any negative signal overrides
        return !_negativeSignals.Any(s => prompt.Contains(s, StringComparison.OrdinalIgnoreCase)) && _positiveSignals.Any(s => prompt.Contains(s, StringComparison.OrdinalIgnoreCase));
    }

    // ── Phase 1: start workflow ───────────────────────────────────────────────

    /// <summary>
    ///     Builds a plan, assesses capability, stores a session, and returns it.
    ///     The caller surfaces <see cref="WorkflowSession.SkeletonRequestPrompt" /> to the calling AI.
    /// </summary>
    public async Task<WorkflowSession> StartWorkflowAsync(
        string prompt,
        string sessionId,
        string workspaceRoot,
        ResearchContextFormat? researchFormat = null,
        CancellationToken ct = default)
    {
        LogStartingAutoWorkflow(_log, sessionId);

        // Build a plan to understand the task shape
        CodePlan plan = await _planBuilder.BuildPlanAsync(prompt, sessionId, workspaceRoot, researchFormat, ct);
        _planStore.Save(plan);

        // Detect primary language from file path hints in plan steps
        string language = DetectPrimaryLanguage(plan.Steps);
        string taskKind = plan.TaskKind.ToString();
        int symbolCount = await CountSymbolsAsync(plan.Steps.Select(s => s.FilePath), workspaceRoot, ct);

        // Assess capability
        CapabilityScore assessment = _assessor.Assess(taskKind, language, symbolCount, plan.OverallConfidence, workspaceRoot);

        // Build the skeleton request prompt
        string skeletonPrompt = BuildSkeletonRequestPrompt(prompt, plan.Summary);

        WorkflowSession session = new()
        {
            SessionId = sessionId,
            OriginalPrompt = prompt,
            PlanId = plan.PlanId,
            SkeletonRequestPrompt = skeletonPrompt,
            State = WorkflowState.WaitingForSkeleton,
            Language = language,
            TaskKind = taskKind,
            Assessment = assessment
        };

        _sessionStore.Save(session);
        LogWorkflowCapability(_log, sessionId, assessment.Level, assessment.Score);

        return session;
    }

    // ── Phase 2: continue workflow ────────────────────────────────────────────

    /// <summary>
    ///     Receives the AI's skeleton design, executes CanDo plan steps,
    ///     and returns a result containing applied patches and deferred steps.
    /// </summary>
    public async Task<WorkflowResult> ContinueWorkflowAsync(
        string sessionId,
        string aiSkeleton,
        string workspaceRoot,
        CancellationToken ct = default)
    {
        WorkflowSession session = _sessionStore.Get(sessionId)
                      ?? throw new InvalidOperationException($"Workflow session '{sessionId}' not found.");

        if (session.State != WorkflowState.WaitingForSkeleton)
        {
            throw new InvalidOperationException(
                $"Session '{sessionId}' is in state {session.State}, expected WaitingForSkeleton.");
        }

        session.State = WorkflowState.InProgress;

        CodePlan plan = (session.PlanId is not null ? _planStore.Get(session.PlanId) : null) ?? throw new InvalidOperationException($"Plan '{session.PlanId}' not found for session '{sessionId}'.");
        List<PatchResult> applied = [];
        List<DeferredStep> deferred = [];

        foreach (PlanStep step in plan.Steps)
        {
            // Per-step capability check: real symbol count for the step's own file(s)
            int stepSymbolCount = await CountSymbolsAsync([step.FilePath], workspaceRoot, ct);
            CapabilityScore stepAssessment = _assessor.Assess(
                session.TaskKind,
                session.Language,
                stepSymbolCount,
                step.Confidence,
                workspaceRoot);

            if (stepAssessment.Level == CapabilityLevel.ShouldDefer || step.RequiresAIHelp)
            {
                string handoff = step.AIHandoffPrompt
                              ?? $"Step {step.StepNumber}: {step.Description}\n\nSkeleton context:\n{aiSkeleton}";

                deferred.Add(new DeferredStep(
                    step.StepNumber,
                    step.Description,
                    step.FilePath,
                    handoff));
                continue;
            }

            // Attempt execution; on AI-handoff result, defer
            StepExecutionResult execResult = await _executor.ExecuteStepAsync(plan, step.StepNumber, workspaceRoot, aiSkeleton, ct);

            if (execResult.NeedsAI)
            {
                deferred.Add(new DeferredStep(
                    step.StepNumber,
                    step.Description,
                    step.FilePath,
                    execResult.AIPrompt ?? $"Step {step.StepNumber} requires AI assistance."));
            }
            else if (execResult.Patch is not null)
            {
                applied.Add(execResult.Patch);
            }
        }

        session.State = WorkflowState.Complete;

        string summary = BuildResultSummary(applied, deferred, session.Assessment);
        LogWorkflowComplete(_log, sessionId, applied.Count, deferred.Count);

        return new WorkflowResult(sessionId, applied, deferred, session.Assessment!, summary);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Sums indexed symbol counts across the given (distinct, non-null) file paths — the real
    ///     complexity signal for <see cref="CapabilityAssessor" />. Unindexed files contribute 0,
    ///     not an error, so brand-new files don't artificially spike the complexity penalty.
    /// </summary>
    private async Task<int> CountSymbolsAsync(IEnumerable<string?> filePaths, string workspaceRoot, CancellationToken ct)
    {
        int total = 0;
        foreach (string path in filePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            total += await _index.CountSymbolsInFileAsync(path, workspaceRoot, ct);
        }

        return total;
    }

    private static string DetectPrimaryLanguage(IReadOnlyList<PlanStep> steps)
    {
        IGrouping<string, string>? extensions = steps
            .Select(s => s.FilePath)
            .Where(p => p is not null)
            .Select(p => Path.GetExtension(p!))
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .GroupBy(e => e)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        return extensions?.Key ?? ".cs";
    }

    private static string BuildSkeletonRequestPrompt(string originalPrompt, string? planSummary)
    {
        StringBuilder sb = new();
        _ = sb.AppendLine("Before I implement the following task, please describe a skeleton design:");
        _ = sb.AppendLine();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"**Task:** {originalPrompt}");
        if (!string.IsNullOrWhiteSpace(planSummary))
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"**Context:** {planSummary}");
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("Please describe:");
        _ = sb.AppendLine("- What types, classes, or functions should exist and why");
        _ = sb.AppendLine(
            "- What each type's public interface should look like (signatures only, no implementation bodies)");
        _ = sb.AppendLine("- Key data flows or contracts between components");
        _ = sb.AppendLine();
        _ = sb.AppendLine("Keep it concise — I will implement the details based on your design.");
        return sb.ToString().TrimEnd();
    }

    private static string BuildResultSummary(
        List<PatchResult> applied,
        List<DeferredStep> deferred,
        CapabilityScore? assessment)
    {
        StringBuilder sb = new();
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"CodeHelper implemented **{applied.Count}** step(s) and deferred **{deferred.Count}** to you.");

        if (applied.Count > 0)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine("**Applied:**");
            foreach (PatchResult p in applied)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- {p.FilePath} ({p.LinesChanged} line(s) changed)");
            }
        }

        if (deferred.Count > 0)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine("**Deferred to you:**");
            foreach (DeferredStep d in deferred)
            {
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"- Step {d.StepNumber}: {d.Description}");
            }
        }

        if (assessment is not null)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"*{assessment.Justification}*");
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("After reviewing my output, call **ReportReviewFeedback** to help calibrate future assessments.");

        return sb.ToString().TrimEnd();
    }
}

// ── Result types ──────────────────────────────────────────────────────────────
