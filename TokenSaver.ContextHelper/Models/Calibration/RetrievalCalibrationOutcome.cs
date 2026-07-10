namespace TokenSaver.ContextHelper.Models.Calibration;

/// <summary>
///     One sample appended to <c>.tokensaver/retrieval-calibration-outcomes.jsonl</c> whenever a
///     caller requests a file in a later turn of the same session that <see cref="Pipeline.Stages.SemanticRelevanceFilterStage" />
///     never saw scored above the bar it applied on the previous turn. This is the raw evidence
///     <see cref="Calibration.RetrievalCalibrationService" /> adjusts <c>SemanticThreshold</c> from.
/// </summary>
public sealed class RetrievalCalibrationOutcome
{
    public string Timestamp { get; set; } = "";
    public string? SessionId { get; set; }
    public string Prompt { get; set; } = "";

    /// <summary>The lowest relevance score among chunks kept on the previous turn — the bar the late file is judged against.</summary>
    public double PreviousMinKeptRelevance { get; set; }

    public string LateFile { get; set; } = "";

    /// <summary>The late file's relevance score against the current prompt, computed the same way the filter stage scores chunks.</summary>
    public double LateFileRelevance { get; set; }

    /// <summary><see cref="PreviousMinKeptRelevance" /> minus <see cref="LateFileRelevance" />. Positive means the late file scored lower than everything that was kept.</summary>
    public double Gap { get; set; }

    /// <summary>True when <see cref="Gap" /> exceeded <c>NarrowFailureMargin</c> — the filter was too narrow and dropped/never-considered a file that turned out to be needed.</summary>
    public bool WasFailure { get; set; }
}
