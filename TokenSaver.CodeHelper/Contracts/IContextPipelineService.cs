using AiDevTools.TokenSaver.TokenEconomy.Models.Context;

namespace AiDevTools.TokenSaver.CodeHelper.Contracts;

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
    Task<BuildContextResult> BuildConciseContextAsync(
        string prompt,
        string[]? history = null,
        string? sessionId = null,
        string[]? requestedFiles = null,
        string? workspaceRoot = null,
        int? maxTokens = null,
        bool avoidChainOfThought = false,
        int perFileTokenLimit = 0,
        CancellationToken ct = default);
}
