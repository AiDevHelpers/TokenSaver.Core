using TokenSaver.CodeHelper.Models.Calibration;

namespace TokenSaver.CodeHelper.Configuration;

public class CodeHelperConfig
{
    // Symbol index
    public string IndexPath { get; set; } = ".tokensaver/indices/code-index.db";
    public IReadOnlyList<string> IndexedExtensions { get; set; } = [".cs", ".ts", ".js", ".py", ".go", ".java"];

    public IReadOnlyList<string> ExcludePatterns { get; set; } =
        ["bin/", "obj/", "node_modules/", ".git/", "dist/", "out/", ".tokensaver/"];

    // Patch engine
    public string BackupPath { get; set; } = ".tokensaver/backups";
    public bool RequireAnchorMatch { get; set; } = true;
    public int MaxPatchFilesPerBatch { get; set; } = 20;

    // Agentic planner
    public int MaxPlanSteps { get; set; } = 30;
    public double ConfidenceThreshold { get; set; } = 0.65; // below → hand off to calling AI
    public double ModelCallThreshold { get; set; } = 0.55; // below → invoke Ollama for plan

    // ── Auto-workflow ──────────────────────────────────────────────────────────

    /// <summary>Enable auto-workflow intercept for code-build prompts. On by default.</summary>
    public bool AutoEnabled { get; set; } = true;

    /// <summary>Seconds to wait for the calling AI to return a skeleton before timing out.</summary>
    public int SkeletonTimeoutSec { get; set; } = 60;

    // ── Capability thresholds ──────────────────────────────────────────────────

    /// <summary>Composite score at or above which CodeHelper claims full ownership of a step.</summary>
    public double CanDoThreshold { get; set; } = 0.70;

    /// <summary>Composite score below which CodeHelper defers the step entirely to the AI.</summary>
    public double DeferThreshold { get; set; } = 0.40;

    // ── Calibration ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Fraction of work the AI had to redo before the scaffold counts as "not worth it".
    ///     If correctionRatio >= WasteThreshold, calibration weights are decremented.
    /// </summary>
    public double WasteThreshold { get; set; } = 0.40;

    /// <summary>Step size for weight updates each time an outcome is recorded (0.0–1.0).</summary>
    public double LearningRate { get; set; } = 0.10;

    /// <summary>Absolute path to the user-level calibration file. Empty = platform default.</summary>
    public string UserCalibrationPath { get; set; } = "";

    /// <summary>Workspace-relative path to the workspace-level calibration file.</summary>
    public string WorkspaceCalibrationPath { get; set; } = ".tokensaver/calibration.json";

    // ── Per-task-kind calibration policies ────────────────────────────────────

    /// <summary>
    ///     Per-task-kind calibration policy overrides. Key = PlanTaskKind.ToString().
    ///     Tasks not listed use LearningRateMultiplier=1.0 and MinSamplesBeforeAdjust=1.
    /// </summary>
    public Dictionary<string, TaskKindCalibrationPolicy> TaskKindPolicies { get; set; } = new()
    {
        // Renames are highly reliable — small learning rate, adjust after just 1 sample
        ["Rename"] = new TaskKindCalibrationPolicy { LearningRateMultiplier = 0.5, MinSamplesBeforeAdjust = 1 },
        // Test generation is consistent — moderate learning
        ["AddTests"] = new TaskKindCalibrationPolicy { LearningRateMultiplier = 0.8, MinSamplesBeforeAdjust = 2 },
        // Dead-code deletion is reliable — slow to adjust (don't erode a good weight)
        ["DeleteDead"] = new TaskKindCalibrationPolicy { LearningRateMultiplier = 0.5, MinSamplesBeforeAdjust = 2 },
        // Bug fixes are unpredictable — learn faster, require more samples
        ["BugFix"] = new TaskKindCalibrationPolicy { LearningRateMultiplier = 1.5, MinSamplesBeforeAdjust = 3 },
        // New features vary widely — learn quickly, needs evidence
        ["AddFeature"] = new TaskKindCalibrationPolicy { LearningRateMultiplier = 1.2, MinSamplesBeforeAdjust = 3 },
        // Refactors are moderately predictable
        ["Refactor"] = new TaskKindCalibrationPolicy { LearningRateMultiplier = 1.0, MinSamplesBeforeAdjust = 2 },
        // Extractions are reliable but complex
        ["Extract"] = new TaskKindCalibrationPolicy { LearningRateMultiplier = 0.8, MinSamplesBeforeAdjust = 2 },
        // Reorganize is the least predictable — high multiplier, many samples needed
        ["Reorganize"] = new TaskKindCalibrationPolicy
        { LearningRateMultiplier = 1.8, MinSamplesBeforeAdjust = 5, WasteThresholdOverride = 0.30 },
        // General is a catch-all — learn from everything
        ["General"] = new TaskKindCalibrationPolicy { LearningRateMultiplier = 1.0, MinSamplesBeforeAdjust = 2 }
    };

    public TaskKindCalibrationPolicy GetPolicy(string taskKind)
    {
        return TaskKindPolicies.TryGetValue(taskKind, out TaskKindCalibrationPolicy? p) ? p : new TaskKindCalibrationPolicy();
    }
}
