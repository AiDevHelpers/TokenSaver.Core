using AiDevTools.TokenSaver.TokenEconomy.Models.TokenEconomy;

namespace AiDevTools.TokenSaver.TokenEconomy.Contracts;

/// <summary>
///     Walks a workspace, splits files into chunks (IChunkExtractor), summarizes + embeds each
///     new/changed chunk via Ollama, and upserts them into the shared Chunks table — the
///     "IndexRepo" step SemanticGrep queries against. Incremental: unchanged chunks (by content
///     hash) are skipped; chunks/files that no longer exist are pruned.
/// </summary>
public interface IRepoIndexer
{
    Task<RepoIndexStats> IndexRepoAsync(
        string workspaceRoot,
        string[]? extensions = null,
        int concurrency = 4,
        CancellationToken ct = default);
}
