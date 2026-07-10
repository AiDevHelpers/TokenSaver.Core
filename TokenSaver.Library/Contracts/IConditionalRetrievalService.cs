using TokenSaver.Library.Models.TokenEconomy;

namespace TokenSaver.Library.Contracts;

/// <summary>
///     Composes <see cref="ISemanticSearchService" /> (embed ranking) and <see cref="IRerankService" />
///     (judged rerank) into the conditional-rerank + abstain flow from
///     <c>docs/retrieval-design.md</c> §2/§3 — backs the <c>SemanticGrep</c> MCP tool.
/// </summary>
public interface IConditionalRetrievalService
{
    Task<RetrievalOutcome> RetrieveAsync(
        string workspaceRoot,
        string query,
        string? pathFilter,
        int topK,
        CancellationToken ct = default);
}
