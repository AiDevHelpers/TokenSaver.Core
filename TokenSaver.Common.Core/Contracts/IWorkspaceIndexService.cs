namespace TokenSaver.Common.Core.Contracts;

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

/// <summary>
/// Statistics about an indexing operation.
/// </summary>
public sealed record IndexStats
{
    /// <summary>Number of files indexed in this operation.</summary>
    public int FilesIndexed { get; set; }

    /// <summary>Number of symbols extracted from indexed files.</summary>
    public int SymbolsIndexed { get; set; }

    /// <summary>Number of files skipped (no changes detected).</summary>
    public int FilesSkipped { get; set; }

    /// <summary>Number of files removed from index during pruning.</summary>
    public int FilesPruned { get; set; }

    /// <summary>Number of symbols removed from index during pruning.</summary>
    public int SymbolsPruned { get; set; }

    /// <summary>Time elapsed for this indexing operation.</summary>
    public TimeSpan Elapsed { get; set; }
}
