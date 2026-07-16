using TokenSaver.Database.Context;
using TokenSaver.Database.Context.Entities;

using Microsoft.EntityFrameworkCore;

using Xunit;

namespace TokenSaver.TokenEconomy.Tests;

/// <summary>
///     Exercises the Chunks table (<see cref="ChunkEntity" /> via <see cref="WorkspaceIndexContext" />)
///     directly against a real SQLite file under a fresh temp workspace per test — no Ollama
///     dependency, since RepoIndexer/SemanticSearchService's summarize/embed calls aren't exercised
///     here, only the DB layer they both read/write.
/// </summary>
public class ChunksTableTests : IAsyncLifetime
{
    private readonly string _tempWorkspace = Path.Combine(Path.GetTempPath(), "lh-chunks-tests-" + Guid.NewGuid());
    private WorkspaceIndexContext _ctx = null!;

    public async Task InitializeAsync()
    {
        _ctx = await WorkspaceIndexContextFactory.CreateAsync(_tempWorkspace);
    }

    public async Task DisposeAsync()
    {
        await _ctx.DisposeAsync();
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

    private static ChunkEntity MakeChunk(string path = "src/Foo.cs", int start = 1, int end = 10) => new()
    {
        RelativePath = path,
        ChunkType = "function",
        Name = "DoThing",
        StartLine = start,
        EndLine = end,
        ContentHash = "abc123",
        Summary = "Does the thing.",
        EmbeddingVector = "[0.1,0.2,0.3]",
        UpdatedAt = 1700000000000,
    };

    [Fact]
    public async Task Add_ThenQuery_RoundTripsAllFields()
    {
        _ = _ctx.Chunks.Add(MakeChunk());
        _ = await _ctx.SaveChangesAsync();

        ChunkEntity row = await _ctx.Chunks.SingleAsync(c => c.RelativePath == "src/Foo.cs");

        Assert.Equal("function", row.ChunkType);
        Assert.Equal("DoThing", row.Name);
        Assert.Equal(1, row.StartLine);
        Assert.Equal(10, row.EndLine);
        Assert.Equal("abc123", row.ContentHash);
        Assert.Equal("Does the thing.", row.Summary);
        Assert.Equal("[0.1,0.2,0.3]", row.EmbeddingVector);
        Assert.Equal(1700000000000, row.UpdatedAt);
        Assert.True(row.Id > 0);
    }

    [Fact]
    public async Task RelativePathIndex_FiltersToMatchingFileOnly()
    {
        _ctx.Chunks.AddRange(MakeChunk("src/Foo.cs", 1, 10), MakeChunk("src/Foo.cs", 11, 20), MakeChunk("src/Bar.cs", 1, 5));
        _ = await _ctx.SaveChangesAsync();

        List<ChunkEntity> fooChunks = await _ctx.Chunks.Where(c => c.RelativePath == "src/Foo.cs").ToListAsync();

        Assert.Equal(2, fooChunks.Count);
        Assert.All(fooChunks, c => Assert.Equal("src/Foo.cs", c.RelativePath));
    }

    [Fact]
    public async Task UniqueIndex_OnPathAndSpan_RejectsDuplicateInsert()
    {
        _ = _ctx.Chunks.Add(MakeChunk("src/Foo.cs", 1, 10));
        _ = await _ctx.SaveChangesAsync();

        _ = _ctx.Chunks.Add(MakeChunk("src/Foo.cs", 1, 10));

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => _ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task DifferentSpan_SamePath_IsAllowed()
    {
        _ctx.Chunks.AddRange(MakeChunk("src/Foo.cs", 1, 10), MakeChunk("src/Foo.cs", 12, 20));

        int written = await _ctx.SaveChangesAsync();

        Assert.Equal(2, written);
    }

    [Fact]
    public async Task Upsert_ExistingSpan_UpdatesInPlace_MatchingRepoIndexerPattern()
    {
        // Mirrors RepoIndexer's find-or-add-then-mutate upsert against (RelativePath, StartLine, EndLine).
        _ = _ctx.Chunks.Add(MakeChunk("src/Foo.cs", 1, 10));
        _ = await _ctx.SaveChangesAsync();

        ChunkEntity? row = await _ctx.Chunks.FirstOrDefaultAsync(
            c => c.RelativePath == "src/Foo.cs" && c.StartLine == 1 && c.EndLine == 10);
        Assert.NotNull(row);
        row!.ContentHash = "changed-hash";
        row.Summary = "Updated summary.";
        _ = await _ctx.SaveChangesAsync();

        List<ChunkEntity> all = await _ctx.Chunks.Where(c => c.RelativePath == "src/Foo.cs").ToListAsync();
        ChunkEntity updated = Assert.Single(all);
        Assert.Equal("changed-hash", updated.ContentHash);
        Assert.Equal("Updated summary.", updated.Summary);
    }

    [Fact]
    public async Task RemoveRange_PrunesStaleChunks_MatchingRepoIndexerPattern()
    {
        _ctx.Chunks.AddRange(MakeChunk("src/Foo.cs", 1, 10), MakeChunk("src/Foo.cs", 12, 20));
        _ = await _ctx.SaveChangesAsync();

        List<ChunkEntity> rows = await _ctx.Chunks.Where(c => c.RelativePath == "src/Foo.cs").ToListAsync();
        List<ChunkEntity> stale = rows.Where(r => r.StartLine == 12).ToList();
        _ctx.Chunks.RemoveRange(stale);
        _ = await _ctx.SaveChangesAsync();

        ChunkEntity remaining = Assert.Single(await _ctx.Chunks.Where(c => c.RelativePath == "src/Foo.cs").ToListAsync());
        Assert.Equal(1, remaining.StartLine);
    }

    [Fact]
    public async Task DeletingAllChunksForAGoneFile_LeavesOtherFilesIntact()
    {
        _ctx.Chunks.AddRange(MakeChunk("src/Foo.cs", 1, 10), MakeChunk("src/Bar.cs", 1, 5));
        _ = await _ctx.SaveChangesAsync();

        List<ChunkEntity> gone = await _ctx.Chunks.Where(c => c.RelativePath == "src/Foo.cs").ToListAsync();
        _ctx.Chunks.RemoveRange(gone);
        _ = await _ctx.SaveChangesAsync();

        List<ChunkEntity> remaining = await _ctx.Chunks.ToListAsync();
        ChunkEntity survivor = Assert.Single(remaining);
        Assert.Equal("src/Bar.cs", survivor.RelativePath);
    }
}
