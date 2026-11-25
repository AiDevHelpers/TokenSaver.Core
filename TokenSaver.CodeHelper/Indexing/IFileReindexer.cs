using AiDevTools.TokenSaver.CodeHelper.Models.Index;

namespace AiDevTools.TokenSaver.CodeHelper.Indexing;

/// <summary>
///     Minimal seam for re-indexing a single file after it is written.
///     Lets consumers like <c>PatchEngine</c> depend on just the re-index call
///     rather than the full <see cref="WorkspaceIndexService" />, which keeps them
///     testable without the heavy indexing dependency graph.
/// </summary>
public interface IFileReindexer
{
    Task<IndexStats> IndexFileAsync(string root, string relPath, CancellationToken ct = default);
}
