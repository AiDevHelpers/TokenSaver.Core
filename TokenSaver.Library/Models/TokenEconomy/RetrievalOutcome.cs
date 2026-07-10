namespace TokenSaver.Library.Models.TokenEconomy;

/// <summary>
///     Result of <see cref="Contracts.IConditionalRetrievalService.RetrieveAsync" /> — either a
///     ranked hit list, or an abstain verdict carrying the best-scoring (but still-too-weak)
///     candidate for diagnostics. Exactly one of <see cref="Hits" /> (non-empty) or
///     <see cref="BestCandidate" /> is meaningful, selected by <see cref="Abstained" />.
/// </summary>
public sealed record RetrievalOutcome(
    IReadOnlyList<SemanticSearchHit> Hits,
    bool Abstained,
    SemanticSearchHit? BestCandidate,
    double? Confidence,
    bool Reranked);
