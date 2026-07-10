using TokenSaver.ContextHelper.Models.Calibration;
using TokenSaver.ContextHelper.Models.Diagnostics;
using TokenSaver.Library.Services;

namespace TokenSaver.Library.Models.Context;

public class BuildContextResult
{
    public string ContextText { get; set; } = "";
    public int TokenCount { get; set; }
    public bool Alert { get; set; }
    public string? AlertMessage { get; set; }
    public IReadOnlyList<string> GeneratedFiles { get; set; } = [];
    public ContextHygieneReport? HygieneReport { get; set; }
    public ConversationIndex? ConversationIndex { get; set; }
    public PipelineSummary? PipelineSummary { get; set; }

    /// <summary>
    ///     Result of this call's SemanticIndexBackgroundTrigger.EnsureStartedAsync check — whether
    ///     a first-run background chunk/embedding index build was just kicked off for SemanticGrep.
    /// </summary>
    public SemanticIndexTriggerStatus SemanticIndexStatus { get; set; }

    /// <summary>
    ///     Workspace-relative paths of files whose source has been modified since they were
    ///     last indexed into context.db.
    ///     Non-empty means some context may be stale — consider re-running BuildConciseContext.
    /// </summary>
    public IReadOnlyList<string> StaleFiles { get; set; } = [];

    /// <summary>
    ///     Set when this turn requested at least one file the previous turn's semantic relevance
    ///     filter never surfaced above its threshold — evidence the filter was too narrow. Null
    ///     means there was nothing to compare (no session, first turn, or nothing new requested).
    ///     See <see cref="TokenSaver.ContextHelper.Calibration.RetrievalCalibrationService" />.
    /// </summary>
    public RetrievalCalibrationCheckResult? CalibrationCheck { get; set; }
}
