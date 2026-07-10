using TokenSaver.Library.Configuration;
using TokenSaver.Library.Contracts;
using TokenSaver.Library.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace TokenSaver.Library.Tests;

/// <summary>
///     RerankService's prompt-building/response-parsing, offline via FakeOllamaHandler (same
///     pattern as SemanticSearchServiceTests) — no live Ollama. The judged-scoring approach itself
///     (LLM prompt vs a real cross-encoder) is explained in RerankService's doc comment.
/// </summary>
public sealed class RerankServiceTests
{
    private readonly FakeOllamaHandler _fakeHandler = new();

    private RerankService CreateService()
    {
        HttpClient httpClient = new(_fakeHandler) { BaseAddress = new Uri("http://fake-ollama.local") };
        TokenSaverConfig config = new();
        OllamaService ollama = new(httpClient, Options.Create(config), NullLogger<OllamaService>.Instance);
        ModelRouteResolver modelRoutes = new(Options.Create(config));
        return new RerankService(ollama, modelRoutes);
    }

    [Fact]
    public async Task ScoreAsync_NoCandidates_ReturnsEmptyListWithoutCallingModel()
    {
        RerankService service = CreateService();

        IReadOnlyList<double>? scores = await service.ScoreAsync("query", []);

        Assert.NotNull(scores);
        Assert.Empty(scores);
        Assert.Empty(_fakeHandler.GeneratePrompts);
    }

    [Fact]
    public async Task ScoreAsync_ValidJsonArrayResponse_ReturnsScoresInOrder()
    {
        _fakeHandler.GenerateResponder = _ => "[0.1, 0.9, 0.5]";
        RerankService service = CreateService();

        IReadOnlyList<double>? scores = await service.ScoreAsync("query", ["a", "b", "c"]);

        Assert.Equal([0.1, 0.9, 0.5], scores);
    }

    [Fact]
    public async Task ScoreAsync_ResponseWrappedInProse_ExtractsJsonArray()
    {
        _fakeHandler.GenerateResponder = _ => "Sure, here are the scores:\n[0.2, 0.8]\nHope that helps!";
        RerankService service = CreateService();

        IReadOnlyList<double>? scores = await service.ScoreAsync("query", ["a", "b"]);

        Assert.Equal([0.2, 0.8], scores);
    }

    [Fact]
    public async Task ScoreAsync_ScoresOutOfRange_ClampedTo0To1()
    {
        _fakeHandler.GenerateResponder = _ => "[-0.5, 1.5]";
        RerankService service = CreateService();

        IReadOnlyList<double>? scores = await service.ScoreAsync("query", ["a", "b"]);

        Assert.Equal([0.0, 1.0], scores);
    }

    [Fact]
    public async Task ScoreAsync_WrongCountInResponse_ReturnsNull()
    {
        _fakeHandler.GenerateResponder = _ => "[0.1, 0.9]"; // 3 candidates expected, only 2 returned
        RerankService service = CreateService();

        IReadOnlyList<double>? scores = await service.ScoreAsync("query", ["a", "b", "c"]);

        Assert.Null(scores);
    }

    [Fact]
    public async Task ScoreAsync_UnparsableResponse_ReturnsNull()
    {
        _fakeHandler.GenerateResponder = _ => "I refuse to answer in JSON.";
        RerankService service = CreateService();

        IReadOnlyList<double>? scores = await service.ScoreAsync("query", ["a", "b"]);

        Assert.Null(scores);
    }

    [Fact]
    public async Task ScoreAsync_PromptIncludesQueryAndNumberedCandidates()
    {
        _fakeHandler.GenerateResponder = _ => "[0.5, 0.5]";
        RerankService service = CreateService();

        await service.ScoreAsync("where is the retry logic", ["candidate one", "candidate two"]);

        string prompt = Assert.Single(_fakeHandler.GeneratePrompts);
        Assert.Contains("where is the retry logic", prompt, StringComparison.Ordinal);
        Assert.Contains("1. candidate one", prompt, StringComparison.Ordinal);
        Assert.Contains("2. candidate two", prompt, StringComparison.Ordinal);
    }
}
