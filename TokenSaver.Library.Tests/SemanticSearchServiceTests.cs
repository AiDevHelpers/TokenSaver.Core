using TokenSaver.Library.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;
using TokenSaver.Library.Models.TokenEconomy;
using TokenSaver.Library.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using System.Text.Json;

using Xunit;

namespace TokenSaver.Library.Tests;

/// <summary>
///     SemanticSearchService is the only reader of the Chunks table (backs the SemanticGrep MCP
///     tool). Seeds rows directly via WorkspaceIndexContext so ranking/filtering logic is verified
///     independently of RepoIndexer, then uses FakeOllamaHandler for the query embedding call —
///     no live Ollama required.
/// </summary>
public sealed class SemanticSearchServiceTests : IAsyncLifetime
{
    private readonly string _tempWorkspace = Path.Combine(Path.GetTempPath(), "lh-semsearch-tests-" + Guid.NewGuid());
    private readonly FakeOllamaHandler _fakeHandler = new();
    private WorkspaceIndexContext _ctx = null!;

    public async Task InitializeAsync()
    {
        _ctx = await WorkspaceIndexContextFactory.CreateAsync(_tempWorkspace);
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
        _fakeHandler.Dispose();
        if (Directory.Exists(_tempWorkspace))
        {
            try
            {
                Directory.Delete(_tempWorkspace, recursive: true);
            }
            catch (IOException)
            {
                // best-effort cleanup; SQLite file handles can linger briefly on Windows
            }
        }
    }

    private SemanticSearchService CreateService()
    {
        HttpClient httpClient = new(_fakeHandler) { BaseAddress = new Uri("http://fake-ollama.local") };
        TokenSaverConfig config = new();
        OllamaService ollama = new(httpClient, Options.Create(config), NullLogger<OllamaService>.Instance);
        ModelRouteResolver modelRoutes = new(Options.Create(config));
        TokenEconomyDbAccessor db = new();
        return new SemanticSearchService(ollama, modelRoutes, db);
    }

    private async Task SeedChunkAsync(string path, string name, float[] embedding, string summary = "a chunk")
    {
        _ctx.Chunks.Add(new ChunkEntity
        {
            RelativePath = path,
            ChunkType = "function",
            Name = name,
            StartLine = 1,
            EndLine = 5,
            ContentHash = "h",
            Summary = summary,
            EmbeddingVector = JsonSerializer.Serialize(embedding),
            UpdatedAt = 1700000000000
        });
        _ = await _ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task SearchAsync_EmptyChunksTable_ReturnsEmptyResultNotError()
    {
        SemanticSearchService service = CreateService();

        IReadOnlyList<SemanticSearchHit> hits = await service.SearchAsync(_tempWorkspace, "anything");

        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchAsync_RanksExactVectorMatchFirst()
    {
        await SeedChunkAsync("A.cs", "Alpha", [1f, 0f, 0f]);
        await SeedChunkAsync("B.cs", "Beta", [0f, 1f, 0f]);
        _fakeHandler.EmbedResponder = _ => [1f, 0f, 0f]; // query vector matches "Alpha" exactly

        SemanticSearchService service = CreateService();
        IReadOnlyList<SemanticSearchHit> hits = await service.SearchAsync(_tempWorkspace, "find alpha");

        Assert.Equal("Alpha", hits[0].Name);
        Assert.Equal(1.0, hits[0].Score, precision: 3);
    }

    [Fact]
    public async Task SearchAsync_TopK_LimitsResultCount()
    {
        for (int i = 0; i < 5; i++)
        {
            await SeedChunkAsync($"File{i}.cs", $"Fn{i}", [i, 0f, 0f]);
        }

        _fakeHandler.EmbedResponder = _ => [1f, 0f, 0f];
        SemanticSearchService service = CreateService();

        IReadOnlyList<SemanticSearchHit> hits = await service.SearchAsync(_tempWorkspace, "query", topK: 2);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task SearchAsync_PathFilter_ExcludesNonMatchingFiles()
    {
        await SeedChunkAsync("src/Foo.cs", "Foo", [1f, 0f, 0f]);
        await SeedChunkAsync("src/Bar.cs", "Bar", [1f, 0f, 0f]);
        _fakeHandler.EmbedResponder = _ => [1f, 0f, 0f];
        SemanticSearchService service = CreateService();

        IReadOnlyList<SemanticSearchHit> hits = await service.SearchAsync(_tempWorkspace, "query", pathFilter: "Foo");

        SemanticSearchHit hit = Assert.Single(hits);
        Assert.Equal("src/Foo.cs", hit.RelativePath);
    }

    [Fact]
    public async Task SearchAsync_MalformedEmbeddingVector_TreatedAsZeroScoreNotAnException()
    {
        await SeedChunkAsync("Bad.cs", "Broken", []); // JsonSerializer.Serialize([]) -> "[]", a degenerate vector
        _fakeHandler.EmbedResponder = _ => [1f, 0f, 0f];
        SemanticSearchService service = CreateService();

        IReadOnlyList<SemanticSearchHit> hits = await service.SearchAsync(_tempWorkspace, "query");

        SemanticSearchHit hit = Assert.Single(hits);
        Assert.Equal(0.0, hit.Score);
    }
}
