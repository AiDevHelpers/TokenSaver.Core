namespace AiDevTools.TokenSaver.TokenEconomy.Contracts;

/// <summary>
///     LLM-judged relevance scoring for the embed-score "muddy middle" (see
///     <c>docs/retrieval-design.md</c> §2). Returns one 0.0-1.0 score per candidate, same order as
///     input, or <c>null</c> if the underlying model call failed or its response couldn't be
///     parsed — callers must fall back to the original embed ranking on <c>null</c> rather than
///     treat it as an error.
/// </summary>
public interface IRerankService
{
    Task<IReadOnlyList<double>?> ScoreAsync(
        string query,
        IReadOnlyList<string> candidateSummaries,
        CancellationToken ct = default);
}
