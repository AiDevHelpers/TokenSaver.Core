namespace TokenSaver.Common.Core.Contracts;

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

/// <summary>
/// Result from building concise context.
/// </summary>
public sealed record BuildContextResult
{
    /// <summary>The assembled context text ready for sending to an AI model.</summary>
    public required string ContextText { get; set; }

    /// <summary>Metadata about the context assembly process.</summary>
    public Dictionary<string, object>? Metadata { get; set; }
}
