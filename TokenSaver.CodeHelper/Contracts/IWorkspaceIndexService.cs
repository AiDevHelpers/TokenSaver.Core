using AiDevTools.TokenSaver.CodeHelper.Models.Index;

namespace AiDevTools.TokenSaver.CodeHelper.Contracts;

/// <summary>
/// Service for indexing and searching symbols/files in a workspace.
/// Orchestrates workspace analysis, file parsing, and symbol discovery.
/// </summary>
public interface IWorkspaceIndexService
{
    /// <summary>
    /// Performs an incremental or full index update of the workspace.
    /// Respects git staleness and file modification times.
    /// </summary>
    Task<IndexStats> IndexWorkspaceAsync(string workspaceRoot, CancellationToken ct = default);

    /// <summary>
    /// Indexes exactly one file immediately.
    /// Used when a single file has been modified and needs re-indexing.
    /// </summary>
    Task<IndexStats> IndexFileAsync(string workspaceRoot, string relativeFilePath, CancellationToken ct = default);
}
