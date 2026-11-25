using AiDevTools.TokenSaver.Common.Core.Configuration;
using AiDevTools.TokenSaver.TokenEconomy.Contracts;
using AiDevTools.TokenSaver.TokenEconomy.Models.TokenEconomy;

using Microsoft.Extensions.Options;

namespace AiDevTools.TokenSaver.TokenEconomy.Services;

/// <summary>Default <see cref="IConditionalRetrievalService" /> — see that interface's doc comment.</summary>
public sealed class ConditionalRetrievalService(
    ISemanticSearchService search, IRerankService rerank, IOptions<TokenSaverConfig> config)
    : IConditionalRetrievalService
{
    private readonly TokenEconomyConfig _config = config.Value.TokenEconomy;

    public async Task<RetrievalOutcome> RetrieveAsync(
        string workspaceRoot, string query, string? pathFilter, int topK, CancellationToken ct = default)
    {
        int poolSize = Math.Max(topK, _config.SemanticSearchRerankCandidatePoolSize);
        IReadOnlyList<SemanticSearchHit> candidates = await search.SearchAsync(workspaceRoot, query, pathFilter, poolSize, ct);

        if (candidates.Count == 0)
        {
            return new RetrievalOutcome([], Abstained: false, BestCandidate: null, Confidence: null, Reranked: false);
        }

        double top1 = candidates[0].Score;
        double top2 = candidates.Count > 1 ? candidates[1].Score : 0.0;
        double margin = top1 - top2;
        bool clearWinner = top1 >= _config.SemanticSearchHighConfidence && margin >= _config.SemanticSearchClearMargin;
        bool belowFloor = top1 < _config.SemanticGrepAbstainThreshold;

        IReadOnlyList<SemanticSearchHit> ranked = candidates;
        bool reranked = false;

        // Muddy middle: not a clear embed winner, but not already below the abstain floor either
        // (no point spending a model call reranking noise — see retrieval-design.md §2).
        if (_config.EnableConditionalRerank && !clearWinner && !belowFloor)
        {
            IReadOnlyList<double>? scores = await rerank.ScoreAsync(
                query, candidates.Select(h => h.Summary).ToList(), ct);

            if (scores != null)
            {
                ranked = candidates
                    .Zip(scores, (hit, score) => hit with { Score = score })
                    .OrderByDescending(h => h.Score)
                    .ToList();
                reranked = true;
            }
        }

        List<SemanticSearchHit> topHits = ranked.Take(topK).ToList();

        // Re-check the abstain floor against post-rerank scores too (retrieval-design.md §3: "after
        // rerank (or embed-only), if top probability < ABSTAIN_THRESHOLD").
        if (topHits.Count > 0 && topHits[0].Score < _config.SemanticGrepAbstainThreshold)
        {
            return new RetrievalOutcome([], Abstained: true, BestCandidate: topHits[0], Confidence: topHits[0].Score, reranked);
        }

        return new RetrievalOutcome(topHits, Abstained: false, BestCandidate: null, Confidence: null, reranked);
    }
}
