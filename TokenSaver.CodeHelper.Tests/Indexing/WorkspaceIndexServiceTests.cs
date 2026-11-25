using AiDevTools.TokenSaver.CodeHelper.Indexing;
using AiDevTools.TokenSaver.CodeHelper.Models.Index;

using Xunit;

namespace AiDevTools.TokenSaver.CodeHelper.Tests.Indexing;

/// <summary>
///     End-to-end index tests over a temp workspace and a real SQLite index.
/// </summary>
public sealed class WorkspaceIndexServiceTests : IDisposable
{
    private readonly IndexTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Rescan_UnchangedFiles_AreSkipped()
    {
        await _h.WriteAsync("src/Alpha.cs", "public sealed class Alpha\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        IndexStats second = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        Assert.Equal(0, second.FilesIndexed);
        Assert.True(second.FilesSkipped >= 1, $"expected unchanged file to be skipped, got {second.FilesSkipped}");
    }

    [Fact]
    public async Task Rescan_ModifiedFile_ReplacesOldSymbols()
    {
        await _h.WriteAsync("src/Alpha.cs", "public sealed class Alpha\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        await _h.WriteAsync("src/Alpha.cs", "public sealed class Beta\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        Assert.Empty(await _h.Search.FindSymbolAsync("Alpha", null, null, _h.Root, CancellationToken.None));
        Assert.Single(await _h.Search.FindSymbolAsync("Beta", SymbolKind.Class, null, _h.Root, CancellationToken.None));
    }

    [Fact]
    public async Task Rescan_DeletedFile_IsPruned()
    {
        await _h.WriteAsync("src/Keep.cs", "public sealed class Keep\n{\n}\n");
        await _h.WriteAsync("src/Gone.cs", "public sealed class Gone\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        _h.Delete("src/Gone.cs");
        // Pruning is rate-limited per service instance; a fresh instance prunes on its first pass.
        IndexStats stats = await _h.NewIndexService().IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        Assert.Equal(1, stats.FilesPruned);
        Assert.Empty(await _h.Search.FindSymbolAsync("Gone", null, null, _h.Root, CancellationToken.None));
        Assert.Single(await _h.Search.FindSymbolAsync("Keep", null, null, _h.Root, CancellationToken.None));
    }

    [Fact]
    public async Task Rescan_WithinPruneInterval_DoesNotPruneAgain()
    {
        await _h.WriteAsync("src/Keep.cs", "public sealed class Keep\n{\n}\n");
        await _h.WriteAsync("src/Gone.cs", "public sealed class Gone\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        _h.Delete("src/Gone.cs");
        IndexStats stats = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        Assert.Equal(0, stats.FilesPruned);
    }

    [Fact]
    public async Task TypeScriptFile_IsIndexedEndToEnd()
    {
        await _h.WriteAsync("web/orders.ts", "export class OrderService {\n    public total(): number {\n        return 0;\n    }\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        IReadOnlyList<SymbolEntry> found = await _h.Search.FindSymbolAsync(
            "OrderService", SymbolKind.Class, "web/orders.ts", _h.Root, CancellationToken.None);

        Assert.Single(found);
    }

    [Fact]
    public async Task PythonFile_IsIndexedEndToEnd()
    {
        await _h.WriteAsync("tools/parser.py", "class Parser:\n    pass\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        Assert.Single(await _h.Search.FindSymbolAsync("Parser", SymbolKind.Class, null, _h.Root, CancellationToken.None));
    }

    [Fact]
    public async Task FindSymbol_KindFilter_ExcludesOtherKinds()
    {
        await _h.WriteAsync("src/Foo.cs", "public interface IFoo { }\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        Assert.Empty(await _h.Search.FindSymbolAsync("IFoo", SymbolKind.Class, null, _h.Root, CancellationToken.None));
        Assert.Single(await _h.Search.FindSymbolAsync("IFoo", SymbolKind.Interface, null, _h.Root, CancellationToken.None));
    }

    [Fact]
    public async Task FindSymbol_FilePathScope_SelectsOneOfDuplicateNames()
    {
        await _h.WriteAsync("src/A/Helper.cs", "public sealed class Helper\n{\n}\n");
        await _h.WriteAsync("src/B/Helper.cs", "public sealed class Helper\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        IReadOnlyList<SymbolEntry> scoped = await _h.Search.FindSymbolAsync(
            "Helper", SymbolKind.Class, "src/B/Helper.cs", _h.Root, CancellationToken.None);

        SymbolEntry only = Assert.Single(scoped);
        Assert.Equal("src/B/Helper.cs", only.FilePath);
    }

    [Fact]
    public async Task FindSymbol_IsCaseInsensitive()
    {
        await _h.WriteAsync("src/Alpha.cs", "public sealed class Alpha\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        Assert.Single(await _h.Search.FindSymbolAsync("alpha", null, null, _h.Root, CancellationToken.None));
    }

    [Fact]
    public async Task CountSymbolsInFile_ReturnsPerFileCount()
    {
        await _h.WriteAsync("src/Two.cs", "public sealed class One\n{\n}\npublic sealed class Two\n{\n}\n");
        await _h.WriteAsync("src/Other.cs", "public sealed class Solo\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        int two = await _h.Search.CountSymbolsInFileAsync("src/Two.cs", _h.Root, CancellationToken.None);
        int missing = await _h.Search.CountSymbolsInFileAsync("src/NotThere.cs", _h.Root, CancellationToken.None);

        Assert.Equal(2, two);
        Assert.Equal(0, missing);
    }

    [Fact]
    public async Task CountAllSymbols_MatchesIndexedTotal()
    {
        await _h.WriteAsync("src/One.cs", "public sealed class One\n{\n}\n");
        await _h.WriteAsync("src/Two.cs", "public sealed class Two\n{\n}\n");
        IndexStats stats = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        int total = await _h.Search.CountAllSymbolsAsync(_h.Root, CancellationToken.None);

        Assert.Equal(stats.SymbolsIndexed, total);
        Assert.Equal(2, total);
    }

    [Fact]
    public async Task Search_SubstringMatch_FindsNamesContainingQuery()
    {
        await _h.WriteAsync("src/Events.cs", "public sealed class EventPullService\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        IReadOnlyList<SymbolEntry> hits = await _h.Search.SearchAsync("pull", 20, _h.Root, CancellationToken.None);

        Assert.Contains(hits, s => s.Name == "EventPullService");
    }

    [Fact]
    public async Task Search_RespectsLimit()
    {
        await _h.WriteAsync("src/Many.cs",
            "public sealed class WidgetA\n{\n}\npublic sealed class WidgetB\n{\n}\npublic sealed class WidgetC\n{\n}\n");
        _ = await _h.Index.IndexWorkspaceAsync(_h.Root, CancellationToken.None);

        IReadOnlyList<SymbolEntry> hits = await _h.Search.SearchAsync("widget", 2, _h.Root, CancellationToken.None);

        Assert.Equal(2, hits.Count);
    }
}
