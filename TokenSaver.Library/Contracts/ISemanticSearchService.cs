using TokenSaver.Library.Models.TokenEconomy;

namespace TokenSaver.Library.Contracts;

/// <summary>Cosine-similarity search over the Chunks table populated by IRepoIndexer — backs the SemanticGrep tool.</summary>
public interface ISemanticSearchService
{
    Task<IReadOnlyList<SemanticSearchHit>> SearchAsync(
        string workspaceRoot,
        string query,
        string? pathFilter = null,
        int topK = 5,
        CancellationToken ct = default);
}
