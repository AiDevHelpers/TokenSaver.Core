using TokenSaver.Common.Core.Configuration;
using TokenSaver.Database.Context;
using TokenSaver.TokenEconomy.Models.TokenEconomy;
using TokenSaver.TokenEconomy.Services;
using TokenSaver.OllamaIntegration.Tests;
using TokenSaver.OllamaIntegration.Services;
using TokenSaver.Utilities.Services;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Xunit;

namespace TokenSaver.TokenEconomy.Tests;

/// <summary>
///     RepoIndexer is the only writer of the Chunks table — these tests exercise IndexRepoAsync
///     end-to-end (real files on disk, real SQLite index.db) against a FakeOllamaHandler so
///     summarize/embed calls are deterministic and no live Ollama is required. Covers the same
///     write paths a real `IndexRepo` MCP call takes; if the Chunks table ends up empty in a real
///     workspace and these pass, the cause is upstream (IndexRepo never invoked, or the workspace's
///     files don't match the default extension filter) rather than a RepoIndexer bug.
/// </summary>
public sealed class RepoIndexerTests : IDisposable
{
    private readonly string _tempWorkspace = Path.Combine(Path.GetTempPath(), "lh-repoindexer-tests-" + Guid.NewGuid());
    private readonly FakeOllamaHandler _fakeHandler = new();
    private readonly TokenEconomyDbAccessor _db = new();

    public void Dispose()
    {
        _db.Dispose();
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

    private RepoIndexer CreateIndexer()
    {
        HttpClient httpClient = new(_fakeHandler) { BaseAddress = new Uri("http://fake-ollama.local") };
        TokenSaverConfig config = new();
        OllamaService ollama = new(httpClient, Options.Create(config), NullLogger<OllamaService>.Instance);
        ModelRouteResolver modelRoutes = new(Options.Create(config));
        return new RepoIndexer(new ChunkExtractor(), ollama, modelRoutes, _db, Options.Create(config));
    }

    private void WriteFile(string relativePath, string content)
    {
        string fullPath = Path.Combine(_tempWorkspace, relativePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    [Fact]
    public async Task IndexRepoAsync_NewCSharpFile_WritesChunkIntoChunksTable()
    {
        WriteFile("Widget.cs", "namespace Foo;\n\npublic sealed class Widget\n{\n    public void DoThing() { }\n}\n");
        RepoIndexer indexer = CreateIndexer();

        RepoIndexStats stats = await indexer.IndexRepoAsync(_tempWorkspace);

        Assert.Equal(1, stats.FilesScanned);
        Assert.True(stats.ChunksIndexed > 0);

        WorkspaceIndexContext ctx = await _db.GetDbAsync(_tempWorkspace, CancellationToken.None);
        List<TokenSaver.Database.Context.Entities.ChunkEntity> rows = ctx.Chunks.ToList();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.RelativePath == "Widget.cs");
    }

    [Fact]
    public async Task IndexRepoAsync_SecondRunUnchanged_SkipsInsteadOfReindexing()
    {
        WriteFile("Widget.cs", "namespace Foo;\n\npublic sealed class Widget\n{\n    public void DoThing() { }\n}\n");
        RepoIndexer indexer = CreateIndexer();

        RepoIndexStats first = await indexer.IndexRepoAsync(_tempWorkspace);
        int generateCallsAfterFirst = _fakeHandler.GeneratePrompts.Count;

        RepoIndexStats second = await indexer.IndexRepoAsync(_tempWorkspace);

        Assert.Equal(0, second.ChunksIndexed);
        Assert.Equal(first.ChunksIndexed, second.ChunksSkipped);
        Assert.Equal(generateCallsAfterFirst, _fakeHandler.GeneratePrompts.Count); // no new Ollama calls on the unchanged pass
    }

    [Fact]
    public async Task IndexRepoAsync_FileContentChanges_ReindexesOnlyThatChunk()
    {
        WriteFile("Widget.cs", "namespace Foo;\n\npublic sealed class Widget\n{\n    public void DoThing() { }\n}\n");
        RepoIndexer indexer = CreateIndexer();
        _ = await indexer.IndexRepoAsync(_tempWorkspace);

        WriteFile("Widget.cs", "namespace Foo;\n\npublic sealed class Widget\n{\n    public void DoOtherThing() { }\n}\n");
        RepoIndexStats second = await indexer.IndexRepoAsync(_tempWorkspace);

        Assert.True(second.ChunksIndexed > 0);
        WorkspaceIndexContext ctx = await _db.GetDbAsync(_tempWorkspace, CancellationToken.None);
        Assert.Contains(ctx.Chunks, r => r.RelativePath == "Widget.cs" && !string.IsNullOrEmpty(r.ContentHash));
    }

    [Fact]
    public async Task IndexRepoAsync_FileDeletedFromDisk_PrunesItsChunks()
    {
        WriteFile("Widget.cs", "namespace Foo;\n\npublic sealed class Widget\n{\n    public void DoThing() { }\n}\n");
        RepoIndexer indexer = CreateIndexer();
        _ = await indexer.IndexRepoAsync(_tempWorkspace);

        File.Delete(Path.Combine(_tempWorkspace, "Widget.cs"));
        RepoIndexStats second = await indexer.IndexRepoAsync(_tempWorkspace);

        Assert.True(second.ChunksDeleted > 0);
        WorkspaceIndexContext ctx = await _db.GetDbAsync(_tempWorkspace, CancellationToken.None);
        Assert.DoesNotContain(ctx.Chunks, r => r.RelativePath == "Widget.cs");
    }

    [Fact]
    public async Task IndexRepoAsync_UnrecognizedExtension_IsSkippedEntirely()
    {
        WriteFile("readme.txt", "just some text, not covered by the default extension filter");
        RepoIndexer indexer = CreateIndexer();

        RepoIndexStats stats = await indexer.IndexRepoAsync(_tempWorkspace);

        Assert.Equal(0, stats.FilesScanned);
        Assert.Equal(0, stats.ChunksIndexed);
    }

    [Fact]
    public async Task IndexRepoAsync_FileUnderIgnoredSegment_IsNeverScanned()
    {
        WriteFile("node_modules/pkg/index.js", "module.exports = function foo() { return 1; };");
        RepoIndexer indexer = CreateIndexer();

        RepoIndexStats stats = await indexer.IndexRepoAsync(_tempWorkspace);

        Assert.Equal(0, stats.FilesScanned);
    }

    [Fact]
    public async Task IndexRepoAsync_ModelCallTimesOut_RetriesAndStillIndexesChunk()
    {
        // HttpClient request timeouts surface as TaskCanceledException, not HttpRequestException.
        // The first generate call times out; the retry must succeed and the run must not abort.
        int calls = 0;
        _fakeHandler.GenerateResponder = _ =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                throw new TaskCanceledException("HttpClient request timed out");
            }

            return "summary";
        };
        WriteFile("Widget.cs", "namespace Foo;\n\npublic sealed class Widget\n{\n    public void DoThing() { }\n}\n");
        RepoIndexer indexer = CreateIndexer();

        RepoIndexStats stats = await indexer.IndexRepoAsync(_tempWorkspace);

        Assert.Equal(0, stats.ChunksFailed);
        Assert.True(stats.ChunksIndexed > 0);
        Assert.True(calls > 1);
    }

    [Fact]
    public async Task IndexRepoAsync_MultipleFiles_ScansEverySupportedFileAndStoresEveryChunk()
    {
        WriteFile("A.cs", "namespace Foo;\n\npublic sealed class A\n{\n    public void One() { }\n}\n");
        WriteFile("sub/B.cs", "namespace Foo;\n\npublic sealed class B\n{\n    public void Two() { }\n}\n");
        WriteFile("doc.md", "# Title\nintro\n## Section A\ncontent A\n");
        WriteFile("readme.txt", "not a supported extension");
        WriteFile("node_modules/pkg/index.js", "module.exports = 1;");
        RepoIndexer indexer = CreateIndexer();

        RepoIndexStats stats = await indexer.IndexRepoAsync(_tempWorkspace);

        Assert.Equal(3, stats.FilesScanned);
        Assert.Equal(0, stats.ChunksFailed);
        WorkspaceIndexContext ctx = await _db.GetDbAsync(_tempWorkspace, CancellationToken.None);
        List<TokenSaver.Database.Context.Entities.ChunkEntity> rows = ctx.Chunks.ToList();
        Assert.Equal(stats.ChunksIndexed, rows.Count);
        Assert.Contains(rows, r => r.RelativePath == "A.cs");
        Assert.Contains(rows, r => r.RelativePath.EndsWith("B.cs", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.RelativePath == "doc.md");
        Assert.DoesNotContain(rows, r => r.RelativePath.EndsWith("readme.txt", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.RelativePath.Contains("node_modules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task IndexRepoAsync_UnchangedRerunAcrossFiles_SkipsEveryKnownChunk()
    {
        WriteFile("A.cs", "namespace Foo;\n\npublic sealed class A\n{\n    public void One() { }\n}\n");
        WriteFile("doc.md", "# Title\nintro\n## Section A\ncontent A\n");
        RepoIndexer indexer = CreateIndexer();
        RepoIndexStats first = await indexer.IndexRepoAsync(_tempWorkspace);

        RepoIndexStats second = await indexer.IndexRepoAsync(_tempWorkspace);

        Assert.Equal(0, second.ChunksIndexed);
        Assert.Equal(first.ChunksIndexed, second.ChunksSkipped);
        Assert.Equal(0, second.ChunksDeleted);
    }

    [Fact]
    public async Task IndexRepoAsync_MarkdownFile_ChunksBySection()
    {
        WriteFile("doc.md", "# Title\nintro\n## Section A\ncontent A\n");
        RepoIndexer indexer = CreateIndexer();

        RepoIndexStats stats = await indexer.IndexRepoAsync(_tempWorkspace);

        Assert.True(stats.ChunksIndexed >= 1);
        WorkspaceIndexContext ctx = await _db.GetDbAsync(_tempWorkspace, CancellationToken.None);
        Assert.Contains(ctx.Chunks, r => r.RelativePath == "doc.md" && r.ChunkType == "md_section");
    }
}
