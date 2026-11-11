using TokenSaver.Common.Core.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.OllamaIntegration.Tests;
using TokenSaver.TokenEconomy.Services;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace TokenSaver.TokenEconomy.Tests;

/// <summary>
///     Proves <see cref="SemanticQualitySelfTest" /> separates a model that understands the code from
///     one that does not, using fake models so no live model is needed.
/// </summary>
public sealed class SemanticQualitySelfTestTests : IDisposable
{
    private readonly TokenEconomyDbAccessor _db = new();

    public void Dispose() => _db.Dispose();

    private SemanticQualitySelfTest Create(Func<string, string> summarize, Func<string, float[]> embed)
    {
        FakeOllamaHandler handler = new() { GenerateResponder = summarize, EmbedResponder = embed };
        TokenSaverConfig config = new();
        OllamaService ollama = new(
            new HttpClient(handler) { BaseAddress = new Uri("http://fake-ollama.local") },
            Options.Create(config), NullLogger<OllamaService>.Instance);
        ModelRouteResolver routes = new(Options.Create(config));
        RepoIndexer indexer = new(new ChunkExtractor(), ollama, routes, _db, Options.Create(config));
        return new SemanticQualitySelfTest(indexer, new SemanticSearchService(ollama, routes, _db), _db);
    }

    [Fact]
    public async Task RunAsync_ModelThatUnderstandsCode_Passes()
    {
        SemanticQualitySelfTest test = Create(SemanticQualityFakes.GoodSummary, SemanticQualityFakes.BagOfWordsEmbed);

        SemanticQualityResult result = await test.RunAsync();

        Assert.True(result.Passed, result.Narrative);
        Assert.Equal(4, result.Cases.Count);
        Assert.All(result.Cases, c => Assert.True(c.SummaryOk && c.RetrievalOk));
    }

    [Fact]
    public async Task RunAsync_ConstantSummary_FailsOnTermsAndDistinctness()
    {
        SemanticQualitySelfTest test = Create(_ => "This function does something.", SemanticQualityFakes.BagOfWordsEmbed);

        SemanticQualityResult result = await test.RunAsync();

        Assert.False(result.Passed);
        Assert.All(result.Cases, c => Assert.False(c.SummaryOk));
        Assert.Contains("not distinct", result.Narrative, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ModelThatRamblesAroundTheDescription_IsCleanedAndPasses()
    {
        SemanticQualitySelfTest test = Create(
            p => "Here's the revised code with a one-liner:\n\n" + SemanticQualityFakes.GoodSummary(p)
                + "\n\nExplanation of changes:\n```csharp\nint x;\n```\n- one\n- two",
            SemanticQualityFakes.BagOfWordsEmbed);

        SemanticQualityResult result = await test.RunAsync();

        Assert.True(result.Passed, result.Narrative);
    }

    [Fact]
    public async Task RunAsync_ReplyThatIsOnlyCode_FailsFormatEvenWhenTermsPresent()
    {
        SemanticQualitySelfTest test = Create(
            p => "```csharp\n// " + SemanticQualityFakes.GoodSummary(p) + "\n```",
            SemanticQualityFakes.BagOfWordsEmbed);

        SemanticQualityResult result = await test.RunAsync();

        Assert.False(result.Passed);
        Assert.All(result.Cases, c => Assert.False(c.SummaryOk));
    }

    [Fact]
    public async Task RunAsync_RightSummariesButUselessEmbeddings_FailsRetrieval()
    {
        SemanticQualitySelfTest test = Create(SemanticQualityFakes.GoodSummary, _ => [1f, 0f, 0f]);

        SemanticQualityResult result = await test.RunAsync();

        Assert.False(result.Passed);
        Assert.All(result.Cases, c => Assert.True(c.SummaryOk));
        Assert.Contains(result.Cases, c => !c.RetrievalOk);
    }
}
