using TokenSaver.CodeHelper.Calibration;
using TokenSaver.CodeHelper.Configuration;
using TokenSaver.CodeHelper.Models.Calibration;

using Microsoft.Extensions.Options;

namespace TokenSaver.CodeHelper.AutoWorkflow;

/// <summary>
///     Answers "can CodeHelper handle this?" for a given task, language, and plan confidence.
///     Score = 35% task-kind weight + 30% language weight + 25% plan confidence − 10% complexity penalty.
/// </summary>
public sealed class CapabilityAssessor
{
    private readonly CalibrationService _calibration;
    private readonly CodeHelperConfig _cfg;

    public CapabilityAssessor(CalibrationService calibration, IOptions<CodeHelperConfig> config)
    {
        _calibration = calibration;
        _cfg = config.Value;
    }

    /// <param name="taskKind">TaskKind enum name (e.g. "AddFeature").</param>
    /// <param name="language">Primary file extension (e.g. ".cs").</param>
    /// <param name="symbolCount">Number of distinct symbols in affected files.</param>
    /// <param name="planConfidence">Overall confidence returned by PlanBuilder (0.0–1.0).</param>
    /// <param name="workspaceRoot">Used to load workspace-level calibration weights.</param>
    public CapabilityScore Assess(
        string taskKind,
        string language,
        int symbolCount,
        double planConfidence,
        string? workspaceRoot = null)
    {
        CalibrationWeights weights = _calibration.GetWeights(workspaceRoot);

        double taskWeight = weights.GetTaskKindWeight(taskKind);
        double langWeight = weights.GetLanguageWeight(language);
        double complexPenalty = symbolCount > 50 ? 0.10
            : symbolCount > 20 ? 0.05
            : 0.00;

        // Weighted sum (coefficients sum to 1.0 after penalty deduction)
        double score = (taskWeight * 0.35)
                    + (langWeight * 0.30)
                    + (planConfidence * 0.25)
                    - (complexPenalty * 0.10);

        score = Math.Clamp(score, 0.0, 1.0);

        CapabilityLevel level = score >= _cfg.CanDoThreshold ? CapabilityLevel.CanDo
            : score >= _cfg.DeferThreshold ? CapabilityLevel.CanPartiallyDo
            : CapabilityLevel.ShouldDefer;

        string justification = level switch
        {
            CapabilityLevel.CanDo =>
                $"Score {score:F2} — task kind weight {taskWeight:F2}, language weight {langWeight:F2}, plan confidence {planConfidence:F2}.",
            CapabilityLevel.CanPartiallyDo =>
                $"Score {score:F2} — CodeHelper can handle some steps; complex or uncertain steps will be flagged for AI.",
            _ =>
                $"Score {score:F2} — task is outside CodeHelper's reliable range. Deferring to calling AI."
        };

        return new CapabilityScore(level, score, justification);
    }
}

// ── Value types ───────────────────────────────────────────────────────────────

public enum CapabilityLevel
{
    /// <summary>CodeHelper is confident it can implement this autonomously.</summary>
    CanDo,

    /// <summary>CodeHelper can handle part of it; low-confidence steps go to the AI.</summary>
    CanPartiallyDo,

    /// <summary>Task is too complex or unfamiliar — AI should do it directly.</summary>
    ShouldDefer
}

public record CapabilityScore(
    CapabilityLevel Level,
    double Score,
    string Justification);
