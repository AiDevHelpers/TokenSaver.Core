using TokenSaver.Common.Core.Configuration;
using TokenSaver.TokenEconomy.Contracts;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.TokenEconomy.Services;

using Microsoft.Extensions.Options;

using Xunit;

namespace TokenSaver.TokenEconomy.Tests;

/// <summary>
///     ConditionalRetrievalService's clear-winner/muddy-middle/abstain branching
///     (docs/retrieval-design.md §2/§3) against fake ISemanticSearchService/IRerankService — no
///     live Ollama needed since the branching logic itself is pure. RerankService's own
///     prompt/response parsing is covered separately in RerankServiceTests via FakeOllamaHandler.
/// </summary>
public sealed class ConditionalRetrievalServiceTests
{
    private sealed class FakeSearch : ISemanticSearchService
    {
        public IReadOnlyList<SemanticSearchHit> Hits { get; set; } = [];

        public Task<IReadOnlyList<SemanticSearchHit>> SearchAsync(
            string workspaceRoot, string query, string? pathFilter = null, int topK = 5, CancellationToken ct = default) =>
            Task.FromResult(Hits);
    }

    private sealed class FakeRerank : IRerankService
    {
        public bool WasCalled { get; private set; }
        public IReadOnlyList<double>? ScoresToReturn { get; set; }

        public Task<IReadOnlyList<double>?> ScoreAsync(
            string query, IReadOnlyList<string> candidateSummaries, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.FromResult(ScoresToReturn);
        }
    }

    private static TokenSaverConfig Config() => new()
    {
        TokenEconomy = new TokenEconomyConfig
        {
            SemanticGrepAbstainThreshold = 0.66,
            SemanticSearchHighConfidence = 0.79,
            SemanticSearchClearMargin = 0.05,
            SemanticSearchRerankCandidatePoolSize = 12
        }
    };

    private static ConditionalRetrievalService CreateService(FakeSearch search, FakeRerank rerank, TokenSaverConfig? config = null) =>
        new(search, rerank, Options.Create(config ?? Config()));

    [Fact]
    public async Task RetrieveAsync_ClearWinner_SkipsRerank()
    {
        FakeSearch search = new()
        {
            Hits =
            [
                new SemanticSearchHit("A.cs", "method", "A", 1, 5, "alpha", 0.90),
                new SemanticSearchHit("B.cs", "method", "B", 1, 5, "beta", 0.50)
            ]
        };
        FakeRerank rerank = new() { ScoresToReturn = [0.1, 0.99] }; // would flip the order if called
        ConditionalRetrievalService service = CreateService(search, rerank);

        RetrievalOutcome outcome = await service.RetrieveAsync("root", "find A", null, topK: 5);

        Assert.False(rerank.WasCalled);
        Assert.False(outcome.Reranked);
        Assert.False(outcome.Abstained);
        Assert.Equal("A.cs", outcome.Hits[0].RelativePath);
    }

    [Fact]
    public async Task RetrieveAsync_MuddyMiddle_FiresRerankAndReorders()
    {
        FakeSearch search = new()
        {
            Hits =
            [
                new SemanticSearchHit("A.cs", "method", "A", 1, 5, "alpha", 0.70),
                new SemanticSearchHit("B.cs", "method", "B", 1, 5, "beta", 0.68)
            ]
        };
        FakeRerank rerank = new() { ScoresToReturn = [0.2, 0.95] }; // reranker prefers B over A
        ConditionalRetrievalService service = CreateService(search, rerank);

        RetrievalOutcome outcome = await service.RetrieveAsync("root", "find B", null, topK: 5);

        Assert.True(rerank.WasCalled);
        Assert.True(outcome.Reranked);
        Assert.False(outcome.Abstained);
        Assert.Equal("B.cs", outcome.Hits[0].RelativePath);
        Assert.Equal(0.95, outcome.Hits[0].Score);
    }

    [Fact]
    public async Task RetrieveAsync_BelowFloor_AbstainsWithoutCallingRerank()
    {
        FakeSearch search = new()
        {
            Hits = [new SemanticSearchHit("A.cs", "method", "A", 1, 5, "alpha", 0.40)]
        };
        FakeRerank rerank = new() { ScoresToReturn = [0.9] };
        ConditionalRetrievalService service = CreateService(search, rerank);

        RetrievalOutcome outcome = await service.RetrieveAsync("root", "does this exist", null, topK: 5);

        Assert.False(rerank.WasCalled);
        Assert.True(outcome.Abstained);
        Assert.Equal("A.cs", outcome.BestCandidate!.RelativePath);
        Assert.Equal(0.40, outcome.Confidence);
    }

    [Fact]
    public async Task RetrieveAsync_RerankReturnsNull_FallsBackToEmbedRanking()
    {
        FakeSearch search = new()
        {
            Hits =
            [
                new SemanticSearchHit("A.cs", "method", "A", 1, 5, "alpha", 0.70),
                new SemanticSearchHit("B.cs", "method", "B", 1, 5, "beta", 0.68)
            ]
        };
        FakeRerank rerank = new() { ScoresToReturn = null }; // model call failed / unparsable
        ConditionalRetrievalService service = CreateService(search, rerank);

        RetrievalOutcome outcome = await service.RetrieveAsync("root", "find A", null, topK: 5);

        Assert.True(rerank.WasCalled);
        Assert.False(outcome.Reranked);
        Assert.Equal("A.cs", outcome.Hits[0].RelativePath);
        Assert.Equal(0.70, outcome.Hits[0].Score);
    }

    [Fact]
    public async Task RetrieveAsync_RerankPushesTopBelowFloor_Abstains()
    {
        FakeSearch search = new()
        {
            Hits = [new SemanticSearchHit("A.cs", "method", "A", 1, 5, "alpha", 0.70)]
        };
        FakeRerank rerank = new() { ScoresToReturn = [0.2] }; // reranker disagrees with the embedder
        ConditionalRetrievalService service = CreateService(search, rerank);

        RetrievalOutcome outcome = await service.RetrieveAsync("root", "find A", null, topK: 5);

        Assert.True(outcome.Reranked);
        Assert.True(outcome.Abstained);
        Assert.Equal(0.2, outcome.Confidence);
    }

    [Fact]
    public async Task RetrieveAsync_EmptyCandidates_ReturnsEmptyNotAbstained()
    {
        FakeSearch search = new() { Hits = [] };
        FakeRerank rerank = new();
        ConditionalRetrievalService service = CreateService(search, rerank);

        RetrievalOutcome outcome = await service.RetrieveAsync("root", "anything", null, topK: 5);

        Assert.False(rerank.WasCalled);
        Assert.False(outcome.Abstained);
        Assert.Empty(outcome.Hits);
    }

    [Fact]
    public async Task RetrieveAsync_ConditionalRerankDisabled_NeverCallsRerank()
    {
        TokenSaverConfig config = Config();
        config.TokenEconomy.EnableConditionalRerank = false;
        FakeSearch search = new()
        {
            Hits =
            [
                new SemanticSearchHit("A.cs", "method", "A", 1, 5, "alpha", 0.70),
                new SemanticSearchHit("B.cs", "method", "B", 1, 5, "beta", 0.68)
            ]
        };
        FakeRerank rerank = new() { ScoresToReturn = [0.99, 0.1] };
        ConditionalRetrievalService service = CreateService(search, rerank, config);

        RetrievalOutcome outcome = await service.RetrieveAsync("root", "find A", null, topK: 5);

        Assert.False(rerank.WasCalled);
        Assert.False(outcome.Reranked);
        Assert.Equal("A.cs", outcome.Hits[0].RelativePath);
    }
}
