using TokenSaver.ContextHelper.Models;

namespace TokenSaver.ContextHelper.Configuration;

public class ContextHelperConfig
{
    /// <summary>
    ///     Whether the server builds an initial concise-context cache automatically at startup
    ///     (fire-and-forget, waits for SimpleModelServer to come up first). Default on. False = context
    ///     is only ever built when a client explicitly calls BuildConciseContext.
    /// </summary>
    public bool AutoWarmOnStartup { get; set; } = true;

    /// <summary>
    ///     Whether <c>ContextInterceptMiddleware</c> runs its context-cleaning step (dedup,
    ///     log-strip, code-block-collapse) on every prompt-bearing MCP tool call before
    ///     forwarding it. On by default — independent of <c>CodeHelperConfig.AutoEnabled</c>,
    ///     which gates the middleware's separate draft-model interception step (off by default).
    /// </summary>
    public bool AutoIntercept { get; set; } = true;

    /// <summary>
    ///     When enabled, <see cref="Pipeline.ContextCleanerService" /> saves the raw (pre-pipeline)
    ///     context text and the cleaned (post-pipeline) text to <c>.tokensaver/debug/</c> for every
    ///     run, and logs a warning if the cleaned text did not shrink relative to the raw input.
    ///     Off by default — this is a diagnostic aid for verifying the cleaning pipeline actually
    ///     reduces token count, not something needed in normal operation.
    /// </summary>
    public bool Debug { get; set; }

    public int ContextSizeLimit { get; set; } = 120_000;
    public int VerbatimTurns { get; set; } = 5;
    public int CodeBlockCollapseThreshold { get; set; } = 60; // lines
    public double SemanticThreshold { get; set; } = 0.35;
    public double TokenBudgetMargin { get; set; } = 0.85; // 85% of limit
    public int LogTailLines { get; set; } = 20;
    public double NearDuplicateThreshold { get; set; } = 0.85; // Levenshtein similarity
    public int SpaghettiBlockThreshold { get; set; } = 5; // near-dupes per file before warning
    public double PromptAssessorConfidenceThreshold { get; set; } = 0.6; // below → use model
    public double OverStrippingWasteRatioThreshold { get; set; } = 0.3; // fraction of budget
    public double PreserveVerbatimOverlapThreshold { get; set; } = 0.6; // pre-strip static analysis

    /// <summary>
    ///     Ollama model to use for embedding-based semantic relevance filtering.
    ///     Set to empty string to disable the semantic filter entirely.
    ///     The model must be pulled in Ollama before the feature activates.
    ///     Default: nomic-embed-text (pull with: ollama pull nomic-embed-text).
    /// </summary>
    public string EmbeddingModel { get; set; } = "nomic-embed-text";

    /// <summary>
    ///     Per-TaskType weight profile overrides. When present, these replace the hardcoded
    ///     defaults in <see cref="WeightProfile.For" />. Key = TaskType.ToString().
    ///     Any task type omitted here falls back to the hardcoded default.
    /// </summary>
    public Dictionary<string, WeightProfile> TaskWeightProfiles { get; set; } = [];

    /// <summary>Maximum tokens for a context summary (used by ContextHelper internals).</summary>
    public int MaxContextSize => ContextSizeLimit;

    // ── Retrieval self-tuning (RetrievalCalibrationService) ────────────────────

    /// <summary>
    ///     How far below the previous turn's minimum-kept relevance a late-requested file's score
    ///     must fall before it counts as a narrow-filter failure. E.g. 0.15 means "the file the
    ///     caller had to ask for by name scored at least 0.15 lower than the worst chunk we already
    ///     kept" — a sign the threshold is cutting off content that turns out to matter.
    /// </summary>
    public double NarrowFailureMargin { get; set; } = 0.15;

    /// <summary>Consecutive failures required before <see cref="Calibration.RetrievalCalibrationService" /> nudges the threshold down.</summary>
    public int CalibrationMinSamplesBeforeAdjust { get; set; } = 3;

    /// <summary>Step size applied to <see cref="Models.Calibration.RetrievalCalibrationState.ThresholdAdjustment" /> per adjustment.</summary>
    public double CalibrationLearningRate { get; set; } = 0.02;

    /// <summary>Bounds on <see cref="Models.Calibration.RetrievalCalibrationState.ThresholdAdjustment" /> — keeps auto-tuning from driving the effective threshold to 0 or 1.</summary>
    public double CalibrationMaxAdjustment { get; set; } = 0.15;

    /// <summary>Returns the weight profile for a task type, merging config overrides over the defaults.</summary>
    public WeightProfile GetWeightProfile(TaskType taskType)
    {
        return TaskWeightProfiles.TryGetValue(taskType.ToString(), out WeightProfile? profile) ? profile : WeightProfile.For(taskType);
    }
}
