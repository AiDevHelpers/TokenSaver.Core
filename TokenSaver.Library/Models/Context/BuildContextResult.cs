using TokenSaver.ContextHelper.Models.Diagnostics;

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
    ///     Workspace-relative paths of files whose source has been modified since they were
    ///     last indexed into context.db.
    ///     Non-empty means some context may be stale — consider re-running BuildConciseContext.
    /// </summary>
    public IReadOnlyList<string> StaleFiles { get; set; } = [];
}
