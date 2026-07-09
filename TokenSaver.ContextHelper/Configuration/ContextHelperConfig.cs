using TokenSaver.ContextHelper.Models;

namespace TokenSaver.ContextHelper.Configuration;

public class ContextHelperConfig
{
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

    /// <summary>Returns the weight profile for a task type, merging config overrides over the defaults.</summary>
    public WeightProfile GetWeightProfile(TaskType taskType)
    {
        return TaskWeightProfiles.TryGetValue(taskType.ToString(), out WeightProfile? profile) ? profile : WeightProfile.For(taskType);
    }
}
