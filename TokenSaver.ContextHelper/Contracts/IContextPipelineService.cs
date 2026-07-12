namespace TokenSaver.ContextHelper.Contracts;

/// <summary>
/// Service for building concise context through a pipeline of processing stages.
/// Orchestrates filtering, deduplication, and optimization of context content.
/// </summary>
public interface IContextPipelineService
{
    /// <summary>
    /// Builds concise, optimized context from workspace files and semantic search.
    /// Applies all pipeline stages: deduplication, log stripping, relevance filtering, etc.
    /// </summary>
    Task<string> BuildConciseContextAsync(
        string workspaceRoot,
        string userPrompt,
        string agentType,
        int tokenBudget,
        CancellationToken ct = default);

    /// <summary>
    /// Processes raw context through the pipeline stages.
    /// </summary>
    Task<string> ProcessContextAsync(
        string rawContext,
        int tokenBudget,
        CancellationToken ct = default);

    /// <summary>
    /// Gets the current pipeline stages and their configuration.
    /// </summary>
    IReadOnlyList<string> GetPipelineStages();
}
